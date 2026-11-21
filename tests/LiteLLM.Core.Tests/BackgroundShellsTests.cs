using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LiteLLM.Core;
using Xunit;

namespace LiteLLM.Core.Tests;

/// <summary>A turn can run the project and then test it, which needs a program that outlives the call that started
/// it. These drive real shells: the point of the class is that starting returns at once, that the output is in a
/// file the extension can read while it is still being written, and that the next call can send the program a real
/// request — none of which a fake would answer.</summary>
public sealed class BackgroundShellsTests
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "litellm-shells-" + Guid.NewGuid().ToString("N"));

    public BackgroundShellsTests() => Directory.CreateDirectory(_root);

    private static async Task WaitUntilAsync(Func<bool> condition, string what, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(50);
        }
        throw new TimeoutException("Timed out after " + seconds + " seconds waiting for " + what + ".");
    }

    private static async Task<ShellCompletion> WaitForCompletionAsync(BackgroundShells shells, string owner, int seconds = 30)
    {
        ShellCompletion? found = null;
        await WaitUntilAsync(() =>
        {
            var drained = shells.Drain(owner);
            if (drained.Count == 0) return false;
            found = drained[0];
            return true;
        }, "a completion for " + owner, seconds);
        return found!;
    }

    private static async Task<string> WaitForLogAsync(BackgroundShells shells, string path, string text, int seconds = 30)
    {
        var last = "";
        await WaitUntilAsync(() =>
        {
            var read = shells.ReadLog(path, 1, 50, false);
            if (read.Found) last = read.Text;
            return last.IndexOf(text, StringComparison.Ordinal) >= 0;
        }, "\"" + text + "\" in " + Path.GetFileName(path), seconds);
        return last;
    }

    /// <summary>The whole difference from a foreground command: the call returns while the program is still
    /// working, and what it printed is in the log it was given.</summary>
    [Fact]
    public async Task AStartedCommandReturnsBeforeItEndsAndItsOutputIsInItsLog()
    {
        using var shells = new BackgroundShells(_root);
        var started = shells.Start(CommandShell.PowerShell,
            "Write-Output 'ready-line'; Start-Sleep -Milliseconds 1200; Write-Output 'done-line'", _root, "chat-a");

        var running = shells.Running("chat-a");
        Assert.Single(running);
        Assert.Equal(started.Id, running[0].Id);
        Assert.True(started.ProcessId > 0);
        Assert.True(File.Exists(started.LogPath), "the log was created before the shell started");

        var log = await WaitForLogAsync(shells, started.LogPath, "ready-line");
        Assert.Contains("ready-line", log, StringComparison.Ordinal);

        var completion = await WaitForCompletionAsync(shells, "chat-a");
        Assert.Equal(started.Id, completion.Id);
        Assert.Equal(started.ProcessId, completion.ProcessId);
        Assert.Equal(0, completion.ExitCode);
        Assert.Equal(started.LogPath, completion.LogPath);
        Assert.Contains("done-line", shells.ReadLog(started.LogPath, 1, 10, false).Text, StringComparison.Ordinal);
        Assert.Empty(shells.Running("chat-a"));
    }

    /// <summary>A stop the turn asked for is a result it already has. It must not come back as a completion
    /// notice, or the model is told to read a log of a shell it chose to close.</summary>
    [Fact]
    public async Task StoppingAShellEndsItWithoutReportingACompletion()
    {
        using var shells = new BackgroundShells(_root);
        var started = shells.Start(CommandShell.PowerShell, "Start-Sleep -Seconds 120", _root, "chat-a");

        // By process id, because that is what the model is told to use: "taskkill /PID <pid> /T /F".
        Assert.True(shells.Stop(started.ProcessId.ToString()));
        await WaitUntilAsync(() => !shells.AnyRunning, "the stopped shell to end");
        Assert.Empty(shells.Running("chat-a"));

        // The exit event still arrives, on another thread. A completion for a deliberate stop would land here.
        await Task.Delay(400);
        Assert.Empty(shells.Drain("chat-a"));
        Assert.False(shells.Stop("999999"));
    }

    /// <summary>A command that fails at once — a bad switch, a program that is not installed — is exactly the case
    /// whose exit code matters, and its process can be over before it is registered.</summary>
    [Fact]
    public async Task ACommandThatFailsAtOnceStillReportsItsExitCode()
    {
        using var shells = new BackgroundShells(_root);
        var started = shells.Start(CommandShell.Cmd, "exit /b 3", _root, "chat-a");

        var completion = await WaitForCompletionAsync(shells, "chat-a");
        Assert.Equal(started.Id, completion.Id);
        Assert.Equal(3, completion.ExitCode);
    }

    [Fact]
    public async Task TheCeilingNamesTheShellsThatAreStillRunning()
    {
        using var shells = new BackgroundShells(_root, maxShells: 1);
        var started = shells.Start(CommandShell.PowerShell, "Start-Sleep -Seconds 120", _root, "chat-a");

        var error = Assert.Throws<InvalidOperationException>(() =>
            shells.Start(CommandShell.Cmd, "echo second", _root, "chat-a"));
        Assert.Contains("maximum", error.Message);
        Assert.Contains(started.Id, error.Message);
        Assert.Contains("taskkill", error.Message);
    }

    [Fact]
    public void AMissingWorkingDirectoryIsRefusedWithItsPath()
    {
        using var shells = new BackgroundShells(_root);
        var missing = Path.Combine(_root, "gone");
        var error = Assert.Throws<DirectoryNotFoundException>(() => shells.Start(CommandShell.Cmd, "echo hi", missing));
        Assert.Contains(missing, error.Message);
    }

    /// <summary>The log is outside the workspace on purpose, so the one file the model was just pointed at has to
    /// be recognised by name alone; anything else must stay outside.</summary>
    [Fact]
    public void OnlyThisSessionsLogsResolve()
    {
        using var shells = new BackgroundShells(_root);
        var started = shells.Start(CommandShell.Cmd, "echo hi", _root, "chat-a");
        var name = Path.GetFileName(started.LogPath);

        Assert.True(shells.TryResolveLogPath(started.LogPath, out var full));
        Assert.Equal(Path.GetFullPath(started.LogPath), full);

        Assert.True(shells.TryResolveLogPath(name, out full));
        Assert.Equal(Path.GetFullPath(started.LogPath), full);

        Assert.True(shells.TryResolveLogPath("\"" + name + "\"", out full));
        Assert.Equal(Path.GetFullPath(started.LogPath), full);

        Assert.False(shells.TryResolveLogPath(Path.Combine("..", "outside.log"), out _));
        Assert.False(shells.TryResolveLogPath(Path.ChangeExtension(started.LogPath, ".txt"), out _));
        Assert.False(shells.TryResolveLogPath("", out _));
        Assert.False(shells.TryResolveLogPath(null, out _));
    }

    /// <summary>Two conversations share one window and one shell manager. A notice about one chat's server must
    /// not be delivered to the other, and must not be delivered twice to its own.</summary>
    [Fact]
    public async Task ACompletionBelongsToTheConversationThatStartedIt()
    {
        using var shells = new BackgroundShells(_root);
        var first = shells.Start(CommandShell.Cmd, "echo a", _root, "chat-a");
        var second = shells.Start(CommandShell.Cmd, "echo b", _root, "chat-b");

        var a = await WaitForCompletionAsync(shells, "chat-a");
        var b = await WaitForCompletionAsync(shells, "chat-b");

        Assert.Equal(first.Id, a.Id);
        Assert.Equal(second.Id, b.Id);
        Assert.Empty(shells.Drain("chat-a"));   // taken once, and only by its owner
        Assert.Empty(shells.Drain("chat-b"));
        Assert.Empty(shells.Running("chat-c"));
    }

    /// <summary>The feature end to end: a real service is started in the background, its log says it is listening,
    /// a real HTTP request goes to it through the same command tool, and the log holds the line the request
    /// produced. This is the loop the guidance in <see cref="AgentPrompt"/> asks the model to run.</summary>
    [Fact]
    public async Task AServiceStartedInTheBackgroundAnswersARealRequest()
    {
        var port = FreePort();
        var script = Path.Combine(_root, "service.ps1");
        File.WriteAllText(script, ServiceScript().Replace("__PORT__", port.ToString()), new UTF8Encoding(false));

        using var shells = new BackgroundShells(_root);
        var started = shells.Start(CommandShell.PowerShell, "& '" + script + "'", _root, "chat-a");
        try
        {
            await WaitForLogAsync(shells, started.LogPath, "listening on port " + port, 60);

            var result = await CommandExecutor.ExecuteAsync(CommandShell.Cmd,
                "curl.exe -s -w \" http-%{http_code}\" http://127.0.0.1:" + port + "/api/hello",
                _root, TimeSpan.FromSeconds(30), CancellationToken.None);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("CALL received", result.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("http-200", result.StandardOutput, StringComparison.Ordinal);

            var log = await WaitForLogAsync(shells, started.LogPath, "REQUEST GET /api/hello");
            Assert.Contains("REQUEST GET /api/hello", log, StringComparison.Ordinal);
        }
        finally
        {
            Assert.True(shells.Stop(started.Id));
        }

        await WaitUntilAsync(() => !shells.AnyRunning, "the service to stop");
        var after = await CommandExecutor.ExecuteAsync(CommandShell.Cmd,
            "curl.exe -s -m 3 http://127.0.0.1:" + port + "/api/hello",
            _root, TimeSpan.FromSeconds(20), CancellationToken.None);
        Assert.True(after.ExitCode != 0 || after.StandardOutput.Length == 0,
            "the service still answered after it was stopped: " + after.StandardOutput);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>A listening socket rather than an HttpListener, which needs a URL reservation the test is not
    /// entitled to make. One request, one answer, and the request line written to the log is the proof the model is
    /// asked to correlate with the status code.</summary>
    private static string ServiceScript() => @"
$port = __PORT__
$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $port)
try { $listener.Start() } catch { Write-Output ""failed: $($_.Exception.Message)""; exit 2 }
Write-Output ""listening on port $port""
while ($true) {
  $client = $listener.AcceptTcpClient()
  $stream = $client.GetStream()
  $reader = [System.IO.StreamReader]::new($stream, [System.Text.Encoding]::ASCII)
  $request = $reader.ReadLine()
  Write-Output ""REQUEST $request""
  $body = [System.Text.Encoding]::UTF8.GetBytes(""CALL received"")
  $head = [System.Text.Encoding]::ASCII.GetBytes(""HTTP/1.1 200 OK`r`nContent-Type: text/plain`r`nContent-Length: $($body.Length)`r`nConnection: close`r`n`r`n"")
  $stream.Write($head, 0, $head.Length)
  $stream.Write($body, 0, $body.Length)
  $stream.Flush()
  $client.Close()
}
";
}
