using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace LiteLLM.Core;

public sealed class ClaudeCodeResult
{
    public string Text { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string Diagnostics { get; set; } = "";
}

public static class ClaudeCodeArguments
{
    public static string Build(string sessionId, string allowedTools, string disallowedTools)
    {
        var a = new List<string> { "-p", "--output-format", "stream-json", "--verbose", "--permission-mode", "dontAsk" };
        AddList(a, "--allowedTools", allowedTools); AddList(a, "--disallowedTools", disallowedTools);
        if (!string.IsNullOrWhiteSpace(sessionId)) { a.Add("--resume"); a.Add(sessionId); }
        return string.Join(" ", a.ConvertAll(Quote));
    }
    private static void AddList(List<string> args, string option, string value)
    { if (!string.IsNullOrWhiteSpace(value)) { args.Add(option); args.Add(value); } }
    public static string Quote(string value)
    {
        if (value.Length > 0 && value.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0) return value;
        var b = new StringBuilder("\""); var slashes = 0;
        foreach (var c in value) { if (c == '\\') { slashes++; continue; } if (c == '"') { b.Append('\\', slashes * 2 + 1).Append(c); slashes = 0; } else { b.Append('\\', slashes).Append(c); slashes = 0; } }
        b.Append('\\', slashes * 2).Append('"'); return b.ToString();
    }
}

public sealed class ClaudeToolPolicyOptions
{
    public bool EnableFileEditing { get; set; }
    public bool ConfirmEachEdit { get; set; }
    public bool EnableTerminalTools { get; set; }
    public bool ConfirmEachCommand { get; set; }

    public void Validate(string allowedTools)
    {
        ClaudeToolPolicy.Validate(allowedTools, EnableFileEditing, ConfirmEachEdit, EnableTerminalTools, ConfirmEachCommand);
    }
}

public static class ClaudeToolPolicy
{
    public static string ComposeWithTrustedExecutable(string configuredTools, string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !Path.IsPathRooted(executablePath))
            throw new ArgumentException("Trusted executable path must be absolute.", nameof(executablePath));
        var fullPath = Path.GetFullPath(executablePath);
        if (fullPath.IndexOfAny(new[] { '"', '\r', '\n', '\0' }) >= 0)
            throw new ArgumentException("Trusted executable path contains unsupported characters.", nameof(executablePath));
        var rule = "Bash(\"" + fullPath + "\" *)";
        return string.IsNullOrWhiteSpace(configuredTools) ? rule : configuredTools.Trim() + "," + rule;
    }
    private static readonly HashSet<string> FileWriteTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Edit", "Write", "NotebookEdit"
    };

    public static IReadOnlyCollection<string> ParseAllowedToolNames(string allowedTools)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(allowedTools)) return names;

        var rule = new StringBuilder();
        var depth = 0;
        Action addRule = () =>
        {
            var name = rule.ToString().Trim();
            rule.Clear();
            var ruleStart = name.IndexOf('(');
            if (ruleStart >= 0) name = name.Substring(0, ruleStart).Trim();
            if (name.Length > 0) names.Add(name);
        };
        foreach (var character in allowedTools)
        {
            if (character == '(') depth++;
            else if (character == ')' && depth > 0) depth--;
            if (depth == 0 && (character == ',' || char.IsWhiteSpace(character))) addRule();
            else rule.Append(character);
        }
        addRule();
        return names;
    }

    public static void Validate(string allowedTools, bool enableFileEditing, bool confirmEachEdit, bool enableTerminalTools, bool confirmEachCommand)
    {
        var names = ParseAllowedToolNames(allowedTools);
        var fileWriteTools = new List<string>();
        foreach (var name in names)
            if (FileWriteTools.Contains(name)) fileWriteTools.Add(name);

        if (fileWriteTools.Count > 0 && !enableFileEditing)
            throw new InvalidOperationException("Claude allowed tools include file-write tools (" + string.Join(", ", fileWriteTools.ToArray()) + "), but Enable file editing is off. Remove those tools from the Claude allowlist or enable file editing.");
        if (fileWriteTools.Count > 0 && confirmEachEdit)
            throw new InvalidOperationException("Claude allowed tools include file-write tools (" + string.Join(", ", fileWriteTools.ToArray()) + "), but noninteractive Claude cannot use the extension's per-edit approval. Remove those tools from the Claude allowlist or explicitly disable Confirm before each edit.");
        var hasBash = false;
        foreach (var name in names)
            if (string.Equals(name, "Bash", StringComparison.OrdinalIgnoreCase)) { hasBash = true; break; }
        if (hasBash && !enableTerminalTools)
            throw new InvalidOperationException("Claude allowed tools include Bash, but Enable terminal tools is off. Remove Bash from the Claude allowlist or enable terminal tools.");
        if (hasBash && confirmEachCommand)
            throw new InvalidOperationException("Claude allowed tools include Bash, but noninteractive Claude cannot use the extension's per-command approval. Remove Bash from the Claude allowlist or explicitly disable Confirm before each command.");
    }
}

public sealed class ClaudeStreamParser
{
    private readonly StringBuilder _text = new StringBuilder();
    public string SessionId { get; private set; } = "";
    public string Text => _text.ToString();
    public bool HasRecognizedRecord { get; private set; }
    public bool Accept(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        JObject o; try { o = JObject.Parse(line); } catch { return false; }
        var before = _text.ToString();
        var sid = o.Value<string>("session_id");
        if (SessionId.Length == 0 && !string.IsNullOrWhiteSpace(sid)) SessionId = sid!;
        var type = o.Value<string>("type") ?? ""; string candidate = "";
        if (type == "assistant")
        {
            var blocks = o.SelectToken("message.content") as JArray;
            if (blocks == null) return false;
            HasRecognizedRecord = true;
            var text = new StringBuilder();
            foreach (var block in blocks)
                if (string.Equals(block.Value<string>("type"), "text", StringComparison.Ordinal)) text.Append(block.Value<string>("text") ?? "");
            candidate = text.ToString();
        }
        else if (type == "result") { HasRecognizedRecord = true; candidate = o.Value<string>("result") ?? ""; }
        if (candidate.Length == 0) return false;
        var current = _text.ToString();
        if (candidate.StartsWith(current, StringComparison.Ordinal)) { _text.Clear(); _text.Append(candidate); }
        else if (!current.EndsWith(candidate, StringComparison.Ordinal)) _text.Append(candidate);
        return !string.Equals(before, _text.ToString(), StringComparison.Ordinal);
    }
}

public sealed class ClaudeCodeExecutable
{
    public string FileName { get; private set; }
    public string ArgumentPrefix { get; private set; }
    private readonly bool _commandScript;
    private ClaudeCodeExecutable(string fileName, string argumentPrefix, bool commandScript = false) { FileName = fileName; ArgumentPrefix = argumentPrefix; _commandScript = commandScript; }

    public static ClaudeCodeExecutable Resolve(string configured, string? path = null, string? pathExt = null, string? comSpec = null)
    {
        if (string.IsNullOrWhiteSpace(configured)) configured = "claude";
        configured = Environment.ExpandEnvironmentVariables(configured.Trim().Trim('"'));
        path = path ?? Environment.GetEnvironmentVariable("PATH") ?? "";
        pathExt = pathExt ?? Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD";
        comSpec = comSpec ?? Environment.GetEnvironmentVariable("ComSpec") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");

        var hasDirectory = configured.IndexOf(Path.DirectorySeparatorChar) >= 0 || configured.IndexOf(Path.AltDirectorySeparatorChar) >= 0 || Path.IsPathRooted(configured);
        var extension = Path.GetExtension(configured);
        var candidates = new List<string>();
        var extensions = new List<string>();
        foreach (var ext in pathExt.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
        { var normalized = ext.Trim(); normalized = normalized.StartsWith(".", StringComparison.Ordinal) ? normalized : "." + normalized; if (!extensions.Exists(x => string.Equals(x, normalized, StringComparison.OrdinalIgnoreCase))) extensions.Add(normalized); }
        if (extensions.Count == 0) extensions.AddRange(new[] { ".com", ".exe", ".bat", ".cmd" });
        if (hasDirectory)
        {
            if (extension.Length > 0) candidates.Add(Path.GetFullPath(configured));
            else foreach (var ext in extensions) candidates.Add(Path.GetFullPath(configured + ext));
        }
        else
        {
            var names = new List<string>();
            if (extension.Length > 0) names.Add(configured); else foreach (var ext in extensions) names.Add(configured + ext);
            // Match Windows search semantics: the first matching file in each PATH
            // directory wins; PATHEXT determines precedence within that directory.
            foreach (var directoryValue in path.Split(new[] { Path.PathSeparator }, StringSplitOptions.None))
            {
                var directory = directoryValue.Trim().Trim('"');
                if (directory.Length == 0) directory = Environment.CurrentDirectory;
                foreach (var name in names) candidates.Add(Path.Combine(directory, name));
            }
        }
        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate)) continue;
            var full = Path.GetFullPath(candidate); var ext = Path.GetExtension(full);
            if (string.Equals(ext, ".cmd", StringComparison.OrdinalIgnoreCase) || string.Equals(ext, ".bat", StringComparison.OrdinalIgnoreCase))
            {
                ClaudeCodeExecutable npmShim;
                if (TryResolveNpmShim(full, path, pathExt, out npmShim)) return npmShim;
                if (!File.Exists(comSpec)) throw new FileNotFoundException("Claude Code uses a command script, but ComSpec was not found: " + comSpec, comSpec);
                // cmd.exe expands percent expressions (and potentially delayed !
                // expansion) even inside quotes. Such script paths cannot be made
                // unambiguous with command-line quoting alone.
                if (full.IndexOfAny(new[] { '%', '!', '\r', '\n' }) >= 0)
                    throw new ArgumentException("The configured Claude command-script path contains characters that cannot be passed safely through cmd.exe. Configure the native claude.exe launcher instead.", nameof(configured));
                return new ClaudeCodeExecutable(comSpec, "/d /s /c \"\"" + full + "\" ", true);
            }
            if (string.Equals(ext, ".exe", StringComparison.OrdinalIgnoreCase) || string.Equals(ext, ".com", StringComparison.OrdinalIgnoreCase)) return new ClaudeCodeExecutable(full, "");
        }
        throw new FileNotFoundException("Claude Code executable '" + configured + "' was not found. Configure an existing .exe/.com/.cmd/.bat path, or add it to PATH. Searched: " + string.Join(", ", candidates.ToArray()), configured);
    }

    private static bool TryResolveNpmShim(string shim, string path, string pathExt, out ClaudeCodeExecutable executable)
    {
        executable = null!;
        string text;
        try { text = File.ReadAllText(shim); } catch { return false; }
        // npm's Windows shim ultimately invokes node with a JavaScript entry point. Resolve
        // both operands ourselves so permission scopes never pass through cmd.exe.
        if (!System.Text.RegularExpressions.Regex.IsMatch(text, @"(?im)^[ \t]*SET[ \t]+""?_prog=[^\r\n]*\bnode(?:\.exe)?\b[^\r\n]*\r?$")) return false;
        var match = System.Text.RegularExpressions.Regex.Match(text,
            @"(?im)^.*""%_prog%""\s+""(?<script>%dp0%\\[^""\r\n]+\.js)""\s+%\*\s*$");
        if (!match.Success) return false;
        var shimDirectory = Path.GetDirectoryName(shim)!;
        var relativeScript = match.Groups["script"].Value.Substring("%dp0%\\".Length);
        var script = Path.GetFullPath(Path.Combine(shimDirectory, relativeScript));
        if (!File.Exists(script)) return false;

        // npm prefers a node.exe installed beside the shim. Otherwise accept only a
        // native PATH result; recursively routing through another script would put the
        // permission scopes back through cmd.exe.
        var adjacentNode = Path.Combine(shimDirectory, "node.exe");
        ClaudeCodeExecutable node;
        if (File.Exists(adjacentNode)) node = new ClaudeCodeExecutable(Path.GetFullPath(adjacentNode), "");
        else try { node = Resolve("node", path, pathExt, null); } catch { return false; }
        if (node.ArgumentPrefix.Length != 0) return false;
        executable = new ClaudeCodeExecutable(node.FileName, ClaudeCodeArguments.Quote(script) + " ");
        return true;
    }

    public string BuildArguments(string arguments)
    {
        if (ArgumentPrefix.Length == 0) return arguments;
        if (!_commandScript) return ArgumentPrefix + arguments;
        // These characters are interpreted by cmd.exe even when they originated in a
        // quoted process argument (% expands inside quotes). Refuse ambiguous script
        // invocations rather than allowing configuration or session data to become
        // command syntax. Native .exe/.com launchers are unaffected.
        if (arguments.IndexOfAny(new[] { '&', '|', '<', '>', '^', '%', '!', '(', ')', '\r', '\n' }) >= 0)
            throw new ArgumentException("Claude Code command-script arguments contain characters that cannot be passed safely through cmd.exe. Configure the native claude.exe launcher instead.", nameof(arguments));
        return ArgumentPrefix + arguments + "\"";
    }
}

public sealed class ClaudeCodeRunner
{
    public async Task<ClaudeCodeResult> RunAsync(string executable, string prompt, string sessionId, string allowedTools, string disallowedTools, string workingDirectory, Action<string> update, Action<string>? sessionStarted, ClaudeToolPolicyOptions? policy, CancellationToken token, string trustedExecutablePath = "")
    {
        if (policy == null) throw new ArgumentNullException(nameof(policy), "Claude tool policy is required at the process-launch boundary.");
        policy.Validate(allowedTools);
            if (!string.IsNullOrWhiteSpace(trustedExecutablePath)) allowedTools = ClaudeToolPolicy.ComposeWithTrustedExecutable(allowedTools, trustedExecutablePath);
        if (string.IsNullOrWhiteSpace(workingDirectory)) throw new ArgumentException("A working directory is required.", nameof(workingDirectory));
        if (!Directory.Exists(workingDirectory)) throw new DirectoryNotFoundException("Claude Code working directory does not exist: " + workingDirectory);
        var resolved = ClaudeCodeExecutable.Resolve(executable);
        var psi = new ProcessStartInfo(resolved.FileName, resolved.BuildArguments(ClaudeCodeArguments.Build(sessionId, allowedTools, disallowedTools)))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            WorkingDirectory = workingDirectory
        };
        using (var process = new Process { StartInfo = psi }) using (var job = new WindowsProcessJob())
        {
            if (!process.Start()) throw new InvalidOperationException("Claude Code could not be started.");
            try { job.Assign(process); }
            catch { try { process.Kill(); } catch { } throw; }
            var parser = new ClaudeStreamParser(); var errors = new StringBuilder(); var noise = new StringBuilder(); var reportedSession = false;
            var stdout = DrainAsync(process.StandardOutput, line =>
            {
                var recognized = parser.HasRecognizedRecord; var changed = parser.Accept(line);
                if (!reportedSession && parser.SessionId.Length > 0) { reportedSession = true; sessionStarted?.Invoke(parser.SessionId); }
                if (!recognized && !parser.HasRecognizedRecord) AppendCapped(noise, line, 4096);
                if (changed) update?.Invoke(parser.Text);
            });
            var stderr = DrainAsync(process.StandardError, line => AppendCapped(errors, line, 4096));
            var wait = WaitAsync(process);
            Task write = Task.CompletedTask;
            var cancelled = new TaskCompletionSource<bool>();
            using (var input = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false)))
            using (token.Register(() =>
            {
                cancelled.TrySetResult(true);
                try { input.Close(); } catch { }
                try { job.Terminate(); } catch { }
            }))
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    write = input.WriteAsync(prompt ?? "");
                    // Observe cancellation and drain failures immediately. Waiting only
                    // on WhenAll can deadlock if stdin blocks or a callback faults while
                    // the child continues filling a redirected pipe.
                    var first = await Task.WhenAny(write, stdout, stderr, cancelled.Task).ConfigureAwait(false);
                    if (first == cancelled.Task) token.ThrowIfCancellationRequested();
                    await first.ConfigureAwait(false);
                    if (first != write) await write.ConfigureAwait(false);
                    input.Close();
                    var all = Task.WhenAll(stdout, stderr, wait, write);
                    first = await Task.WhenAny(all, stdout, stderr, cancelled.Task).ConfigureAwait(false);
                    if (first == cancelled.Task) token.ThrowIfCancellationRequested();
                    await first.ConfigureAwait(false);
                    await all.ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                }
                catch
                {
                    try { process.StandardInput.Close(); } catch { }
                    try { job.Terminate(); } catch { }
                    await ObserveWithinAsync(Task.WhenAll(stdout, stderr, wait, write), TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    if (token.IsCancellationRequested) token.ThrowIfCancellationRequested();
                    throw;
                }
            }
            var diagnostics = errors.ToString().Trim();
            if (process.ExitCode != 0) throw new InvalidOperationException("Claude Code exited with code " + process.ExitCode + ". " + diagnostics);
            if (!parser.HasRecognizedRecord)
            {
                var detail = diagnostics.Length > 0 ? diagnostics : noise.ToString().Trim();
                throw new InvalidOperationException("Claude Code exited successfully but returned no recognizable stream-json records." + (detail.Length > 0 ? " " + detail : ""));
            }
            return new ClaudeCodeResult { Text = parser.Text, SessionId = parser.SessionId, Diagnostics = diagnostics };
        }
    }
    internal static void AppendCapped(StringBuilder buffer, string value, int limit)
    {
        if (buffer.Length >= limit || limit <= 0) return;
        if (buffer.Length > 0 && buffer.Length < limit) buffer.Append('\n');
        var remaining = limit - buffer.Length;
        if (remaining > 0) buffer.Append(value, 0, Math.Min(value.Length, remaining));
    }
    private static async Task DrainAsync(StreamReader reader, Action<string> accept) { string? line; while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null) accept(line); }
    private static Task WaitAsync(Process p) { var t = new TaskCompletionSource<bool>(); p.EnableRaisingEvents = true; p.Exited += (s,e) => t.TrySetResult(true); if (p.HasExited) t.TrySetResult(true); return t.Task; }
    private static async Task ObserveWithinAsync(Task task, TimeSpan timeout)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false);
        if (completed == task) { try { await task.ConfigureAwait(false); } catch { } }
        else _ = task.ContinueWith(t => { var ignored = t.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}

internal sealed class WindowsProcessJob : IDisposable
{
    private IntPtr _handle;
    internal WindowsProcessJob()
    {
        _handle = CreateJobObject(IntPtr.Zero, null); if (_handle == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION(); info.BasicLimitInformation.LimitFlags = 0x2000;
        var size = Marshal.SizeOf(info); var ptr = Marshal.AllocHGlobal(size); try { Marshal.StructureToPtr(info, ptr, false); if (!SetInformationJobObject(_handle, 9, ptr, (uint)size)) throw new System.ComponentModel.Win32Exception(); } finally { Marshal.FreeHGlobal(ptr); }
    }
    internal void Assign(Process p) { if (!AssignProcessToJobObject(_handle, p.Handle)) throw new System.ComponentModel.Win32Exception(); }
    internal void Terminate() { if (_handle != IntPtr.Zero) TerminateJobObject(_handle, 1); }
    public void Dispose() { if (_handle != IntPtr.Zero) { CloseHandle(_handle); _handle = IntPtr.Zero; } }
    [StructLayout(LayoutKind.Sequential)] private struct JOBOBJECT_BASIC_LIMIT_INFORMATION { public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit; public long Affinity; public uint PriorityClass, SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)] private struct IO_COUNTERS { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)] private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION { public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation; public IO_COUNTERS IoInfo; public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll")] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);
    [DllImport("kernel32.dll")] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")] private static extern bool TerminateJobObject(IntPtr job, uint exitCode);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
