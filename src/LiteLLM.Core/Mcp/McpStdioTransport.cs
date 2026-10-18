using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LiteLLM.Core;

/// <summary>A local MCP server, spoken to over its standard input and output.
///
/// The child is launched the same way the Claude CLI is — <see cref="ClaudeCodeExecutable.Resolve"/> finds the
/// command through PATH and unwraps npm's <c>.cmd</c> shim so no third-party script is interpreted by cmd.exe — and
/// assigned to the same <see cref="WindowsProcessJob"/>, so closing Visual Studio takes the server's children with
/// it. stdout carries the newline-delimited JSON; stderr is diagnostics and is drained on its own thread so a chatty
/// server can never fill a pipe and deadlock the turn.</summary>
public sealed class McpStdioTransport : IMcpTransport
{
    private const int MaxStderrLine = 2000;

    private readonly McpServerConfiguration _server;
    private readonly string? _solutionRoot;
    private readonly Action<string>? _diagnostic;
    private readonly SemaphoreSlim _writeGate = new SemaphoreSlim(1, 1);
    private readonly SemaphoreSlim _arrived = new SemaphoreSlim(0);
    private readonly ConcurrentQueue<JObject> _incoming = new ConcurrentQueue<JObject>();
    private Process? _process;
    private WindowsProcessJob? _job;
    private StreamWriter? _input;
    private volatile bool _closed;

    public McpStdioTransport(McpServerConfiguration server, string? solutionRoot = null, Action<string>? diagnostic = null)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _solutionRoot = solutionRoot;
        _diagnostic = diagnostic;
    }

    public string Describe => McpSecrets.Describe(_server);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var problem = _server.ValidationError();
        if (problem != null) throw new McpException(problem);

        ClaudeCodeExecutable executable;
        try { executable = ClaudeCodeExecutable.Resolve(_server.Command); }
        catch (Exception ex) when (ex is FileNotFoundException || ex is ArgumentException)
        {
            throw new McpException("The command for MCP server '" + _server.Name + "' could not be resolved: " + ex.Message);
        }

        var directory = WorkingDirectory();
        var arguments = string.Join(" ", _server.Args.Select(ClaudeCodeArguments.Quote).ToArray());
        var start = new ProcessStartInfo(executable.FileName, executable.BuildArguments(arguments))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            WorkingDirectory = directory
        };
        foreach (var pair in _server.Environment)
            start.EnvironmentVariables[pair.Key] = McpSecrets.Expand(pair.Value, _solutionRoot);

        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        var job = new WindowsProcessJob();
        try
        {
            if (!process.Start()) throw new McpException("MCP server '" + _server.Name + "' could not be started.");
            try { job.Assign(process); }
            catch
            {
                try { process.Kill(); } catch { }
                throw;
            }

            _process = process;
            _job = job;
            _input = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false));
            process.Exited += (sender, args) =>
                _diagnostic?.Invoke("The process for MCP server '" + _server.Name + "' exited.");
            _ = Task.Run(() => ReadStdoutAsync(process));
            _ = Task.Run(() => ReadStderrAsync(process));
            return Task.CompletedTask;
        }
        catch
        {
            job.Dispose();
            process.Dispose();
            throw;
        }
    }

    public async Task SendAsync(JObject message, CancellationToken cancellationToken)
    {
        var input = _input;
        if (input == null || _closed)
            throw new McpException("MCP server '" + _server.Name + "' is not running.");

        var text = message.ToString(Formatting.None) + "\n";
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await input.WriteAsync(text).ConfigureAwait(false);
            await input.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
        {
            throw new McpException("A message could not be written to MCP server '" + _server.Name + "'.", inner: ex);
        }
        finally { _writeGate.Release(); }
    }

    public async Task<JObject?> ReceiveAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            if (_incoming.TryDequeue(out var message)) return message;
            if (_closed) return null;
            try { await _arrived.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (ObjectDisposedException) { return null; }
        }
    }

    /// <summary>stdio has no session to label.</summary>
    public void SetSession(string? sessionId, string? protocolVersion) { }

    private string WorkingDirectory()
    {
        var configured = McpSecrets.Expand(_server.WorkingDirectory, _solutionRoot);
        var directory = string.IsNullOrWhiteSpace(configured) ? _solutionRoot : configured;
        if (string.IsNullOrWhiteSpace(directory)) directory = Environment.CurrentDirectory;
        if (!Directory.Exists(directory))
            throw new McpException("The working directory for MCP server '" + _server.Name + "' does not exist: " + directory);
        return directory!;
    }

    private async Task ReadStdoutAsync(Process process)
    {
        try
        {
            await McpStdioFraming.ReadLinesAsync(process.StandardOutput.BaseStream, AcceptLineAsync,
                line => _diagnostic?.Invoke(line), McpStdioFraming.DefaultMaxLineChars, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (!_closed) _diagnostic?.Invoke("Reading from MCP server '" + _server.Name + "' failed: " + ex.Message);
        }
        finally
        {
            _closed = true;
            _arrived.Release();
        }
    }

    private Task AcceptLineAsync(string line)
    {
        JObject? message = null;
        try { message = JObject.Parse(line); }
        catch (JsonException) { }
        if (message == null)
        {
            // A server that prints a banner on stdout is common enough that this is a diagnostic, not a failure; the
            // next properly framed line still works.
            _diagnostic?.Invoke("A line from MCP server '" + _server.Name + "' was not JSON and was ignored: " +
                                (line.Length > 200 ? line.Substring(0, 200) + "…" : line));
            return Task.CompletedTask;
        }
        _incoming.Enqueue(message);
        _arrived.Release();
        return Task.CompletedTask;
    }

    private async Task ReadStderrAsync(Process process)
    {
        try
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                if (line.Length == 0) continue;
                _diagnostic?.Invoke(line.Length > MaxStderrLine ? line.Substring(0, MaxStderrLine) + "…" : line);
            }
        }
        catch (Exception ex)
        {
            if (!_closed) _diagnostic?.Invoke("Reading the error output of MCP server '" + _server.Name + "' failed: " + ex.Message);
        }
    }

    public void Dispose()
    {
        _closed = true;
        try { _input?.Dispose(); } catch { }
        try { _job?.Terminate(); } catch { }
        try { if (_process != null && !_process.HasExited) _process.Kill(); } catch { }
        try { _process?.WaitForExit(2000); } catch { }
        try { _job?.Dispose(); } catch { }
        try { _process?.Dispose(); } catch { }
        _input = null;
        _job = null;
        _process = null;
        // The semaphores are intentionally not disposed: a receive loop may still be waiting on one, and a semaphore
        // without a wait handle holds no unmanaged resource worth the race.
    }
}
