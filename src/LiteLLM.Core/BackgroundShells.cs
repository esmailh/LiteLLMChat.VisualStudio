using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace LiteLLM.Core;

public sealed class ShellStart
{
    public string Id { get; set; } = "";
    public int ProcessId { get; set; }
    public string LogPath { get; set; } = "";
    public CommandShell Shell { get; set; }
    public string Command { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
}

public sealed class ShellInfo
{
    public string Id { get; set; } = "";
    public int ProcessId { get; set; }
    public bool Running { get; set; }
    public int? ExitCode { get; set; }
    public TimeSpan Duration { get; set; }
    public string LogPath { get; set; } = "";
    public string Command { get; set; } = "";
    /// <summary>The conversation this shell was started for. A shell outlives the turn that started it, and the
    /// notice that it ended belongs to that conversation rather than to whichever one is open now.</summary>
    public string Owner { get; set; } = "";
}

/// <summary>A shell that ended by itself. A deliberate stop is not one of these: the turn asked for it, so it is a
/// result it already has, and announcing it would only make the model read a log it chose to close.</summary>
public sealed class ShellCompletion
{
    public string Id { get; set; } = "";
    public int ProcessId { get; set; }
    public int? ExitCode { get; set; }
    public TimeSpan Duration { get; set; }
    public string LogPath { get; set; } = "";
    public string Owner { get; set; } = "";
    public CommandShell Shell { get; set; }
}

/// <summary>The shells a session has running in the background: an API, a web app, a watcher.
///
/// This is the one thing <see cref="CommandExecutor"/> cannot do — it waits for a command to end, so a server
/// started through it is killed by its own timeout and never answers anything. A shell started here returns as soon
/// as it is running, with its id, its process id and the path of the file its output goes to, and it stays up until
/// it ends on its own or the session does.
///
/// Output is redirected by the shell itself (<c>&gt;</c> and <c>2&gt;&amp;1</c>, PowerShell's <c>*&gt;</c>) rather
/// than pumped through a pipe this process owns: a pipe is a handle the shell and everything it starts inherit, and
/// a server that keeps one open keeps a reader waiting forever. A file has no such reader, and it is still readable
/// after the shell is gone.
///
/// Nothing here decides whether a program may run; the command approval card does, before <see cref="Start"/> is
/// reached.</summary>
public sealed class BackgroundShells : IDisposable
{
    public const int DefaultMaxShells = 8;

    /// <summary>A crashed session leaves its logs behind; they are a debugging aid for a day, not a library.</summary>
    public static readonly TimeSpan KeepLogs = TimeSpan.FromDays(7);

    private sealed class Entry
    {
        public Process Process = null!;
        public string Id = "";
        public int ProcessId;
        public string Command = "";
        public string WorkingDirectory = "";
        public string LogPath = "";
        public string Owner = "";
        public CommandShell Shell;
        public readonly Stopwatch Clock = new Stopwatch();
        public bool Running = true;
        /// <summary>The stop was asked for, so its exit is not news.</summary>
        public bool Silent;
        public int? ExitCode;
    }

    private readonly object _gate = new object();
    private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
    private readonly List<ShellCompletion> _pending = new List<ShellCompletion>();
    private readonly string _folder;
    private readonly int _maxShells;
    private int _autoIds;
    private bool _disposed;

    /// <summary>Raised when a shell ends on its own, from whichever thread the process reported it on. The owner of
    /// the conversation decides what to draw; this class only says what happened.</summary>
    public event EventHandler<ShellCompletion>? Completed;

    public BackgroundShells(string? folder = null, int maxShells = DefaultMaxShells)
    {
        if (maxShells < 1) throw new ArgumentOutOfRangeException(nameof(maxShells));
        _maxShells = maxShells;
        var parent = string.IsNullOrWhiteSpace(folder) ? DefaultFolder : Path.GetFullPath(folder!.Trim());
        Directory.CreateDirectory(parent);
        Sweep(parent);
        // One folder per run: two sessions must not write to the same log file, and the folder is what is removed
        // when the session ends, so nothing is left growing in the user's profile.
        _folder = Path.Combine(parent, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
        Directory.CreateDirectory(_folder);
    }

    /// <summary>Where this session's logs are. Read by the file reader: a log is outside the workspace by design,
    /// and <see cref="ToolExecutionPolicy"/> would otherwise refuse the one file the model was just told to read.</summary>
    public string Folder => _folder;

    public int MaxShells => _maxShells;

    public static string DefaultFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiteLLM.VisualStudio", "shells");

    /// <summary>Starts one command and returns without waiting for it. Throws <see cref="InvalidOperationException"/>
    /// with a sentence the model can act on when the ceiling is reached, and <see cref="DirectoryNotFoundException"/>
    /// when the working directory is gone — a turn should be told why, not handed a shell that died silently.</summary>
    public ShellStart Start(CommandShell shell, string command, string workingDirectory, string? owner = null)
    {
        if (string.IsNullOrWhiteSpace(command)) throw new ArgumentException("A command is required.", nameof(command));
        if (!Directory.Exists(workingDirectory)) throw new DirectoryNotFoundException("Working directory not found: " + workingDirectory);

        ShellStart started;
        Entry? created = null;
        var exitedEarly = false;
        lock (_gate)
        {
            ThrowIfDisposed();
            var running = _entries.Values.Where(entry => entry.Running).ToList();
            if (running.Count >= _maxShells)
                throw new InvalidOperationException(
                    "There are already " + running.Count + " shells running, which is the maximum. Stop one with " +
                    "\"taskkill /PID <pid> /T /F\", or wait for it: " +
                    string.Join(", ", running.Select(entry => entry.Id + " (pid " + entry.ProcessId + ")")) + ".");

            var id = NextId();
            var logPath = Path.Combine(_folder, id + ".log");
            // Created before the shell starts: the model reads this path as soon as it is told it, and a file that
            // does not exist yet reads as a mistake in the path rather than as a shell with nothing to say.
            try { using (File.Create(logPath)) { } }
            catch (IOException) { }

            var entry = new Entry
            {
                Id = id,
                Command = command,
                WorkingDirectory = workingDirectory,
                LogPath = logPath,
                Owner = owner ?? "",
                Shell = shell
            };
            var process = new Process
            {
                StartInfo = CommandExecutor.StartInfoFor(shell, command, workingDirectory, logPath),
                EnableRaisingEvents = true
            };
            entry.Process = process;
            process.Exited += (sender, args) => OnExited(entry);
            try
            {
                if (!process.Start()) throw new InvalidOperationException("The shell could not be started.");
            }
            catch (Exception ex)
            {
                process.Dispose();
                TryDelete(logPath);
                throw new InvalidOperationException("The shell could not be started: " + ex.Message, ex);
            }
            entry.ProcessId = process.Id;
            entry.Clock.Start();
            _entries[id] = entry;
            created = entry;
            try { exitedEarly = process.HasExited; } catch (InvalidOperationException) { }
            started = new ShellStart
            {
                Id = id,
                ProcessId = entry.ProcessId,
                LogPath = logPath,
                Shell = shell,
                Command = command,
                WorkingDirectory = workingDirectory
            };
        }
        // A command that failed at once - a missing executable, a bad switch - must still produce its notice, and the
        // event may have fired before the entry was registered.
        if (exitedEarly && created != null) OnExited(created);
        return started;
    }

    /// <summary>Says whether a path the model sent is one of this session's logs, and gives its full form. A bare
    /// file name is taken as a name in the log folder, so "shell-1.log" works as well as the whole path.</summary>
    public bool TryResolveLogPath(string? path, out string full)
    {
        full = "";
        if (string.IsNullOrWhiteSpace(path)) return false;
        var value = path!.Trim().Trim('"').Trim();
        if (value.Length == 0) return false;
        string candidate;
        try
        {
            candidate = Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(_folder, value));
        }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
        {
            return false;
        }
        if (!candidate.EndsWith(".log", StringComparison.OrdinalIgnoreCase)) return false;
        if (!PathGuard.IsUnder(_folder, candidate)) return false;
        full = candidate;
        return true;
    }

    /// <summary>Reads what a log says now. See <see cref="ShellLog"/> for why it reads from the end by default.</summary>
    public ShellLogRead ReadLog(string path, int startLine, int count, bool fromStart, int maxChars = ShellLog.MaxChars) =>
        ShellLog.Read(path, startLine, count, fromStart, maxChars);

    public IReadOnlyList<ShellInfo> List()
    {
        lock (_gate) return _entries.Values.Select(Info).ToList();
    }

    /// <summary>How many shells this class is holding, running or not. Only the entries are counted: a log file on
    /// disk is the shell's output, not the shell.</summary>
    public int Count { get { lock (_gate) return _entries.Count; } }

    public IReadOnlyList<ShellInfo> Running(string? owner = null)
    {
        lock (_gate)
            return _entries.Values
                .Where(entry => entry.Running && Same(entry.Owner, owner))
                .Select(Info)
                .ToList();
    }

    public bool AnyRunning
    {
        get { lock (_gate) return _entries.Values.Any(entry => entry.Running); }
    }

    /// <summary>The completions nobody has been told about yet, in the order their shells ended. Taking them is what
    /// keeps one notice from being delivered twice; <paramref name="owner"/> leaves the other conversations' notices
    /// where they are.</summary>
    public IReadOnlyList<ShellCompletion> Drain(string? owner = null)
    {
        lock (_gate)
        {
            var taken = _pending.Where(completion => Same(completion.Owner, owner)).ToList();
            foreach (var completion in taken) _pending.Remove(completion);
            return taken;
        }
    }

    /// <summary>Kills one shell and everything it spawned, by id or by process id. Returns false when this session
    /// never started it; a shell that had already ended is still true, because the answer it gives is that it is not
    /// running now.</summary>
    public bool Stop(string? idOrPid)
    {
        var wanted = (idOrPid ?? "").Trim();
        if (wanted.Length == 0) return false;
        Entry? entry;
        lock (_gate)
        {
            ThrowIfDisposed();
            entry = Find(wanted);
            if (entry == null) return false;
            // Set before the kill: the exit event arrives on another thread, and a stop the turn asked for must not
            // come back as a completion notice.
            entry.Silent = true;
            if (!entry.Running) return true;
        }

        // Killed outside the lock: taskkill and WaitForExit take real time, and a read must not wait for a kill.
        CommandExecutor.KillTree(entry.Process);
        try { entry.Process.WaitForExit(5000); }
        catch (InvalidOperationException) { }
        return true;
    }

    public void Dispose()
    {
        List<Entry> entries;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            entries = _entries.Values.ToList();
            foreach (var entry in entries) entry.Silent = true;
        }
        foreach (var entry in entries)
        {
            try { if (entry.Running) CommandExecutor.KillTree(entry.Process); }
            catch (Exception) { }
            try { entry.Process.Dispose(); }
            catch (Exception) { }
        }
        lock (_gate)
        {
            _entries.Clear();
            _pending.Clear();
        }
        // The logs are this session's working material, not a document: a folder per run and removal at the end keeps
        // the profile from growing by a server's whole output on every launch.
        TryDeleteFolder();
    }

    private void OnExited(Entry entry)
    {
        ShellCompletion? completion = null;
        lock (_gate)
        {
            if (!entry.Running) return;
            entry.Running = false;
            try { entry.ExitCode = entry.Process.ExitCode; }
            catch (InvalidOperationException) { }
            entry.Clock.Stop();
            if (entry.Silent) return;
            completion = new ShellCompletion
            {
                Id = entry.Id,
                ProcessId = entry.ProcessId,
                ExitCode = entry.ExitCode,
                Duration = entry.Clock.Elapsed,
                LogPath = entry.LogPath,
                Owner = entry.Owner,
                Shell = entry.Shell
            };
            _pending.Add(completion);
        }
        // Raised outside the lock: the window that draws the row is entitled to read this class while it does.
        if (completion == null) return;
        try { Completed?.Invoke(this, completion); }
        catch (Exception) { }
    }

    private static ShellInfo Info(Entry entry) => new ShellInfo
    {
        Id = entry.Id,
        ProcessId = entry.ProcessId,
        Running = entry.Running,
        ExitCode = entry.ExitCode,
        Duration = entry.Clock.Elapsed,
        LogPath = entry.LogPath,
        Command = entry.Command,
        Owner = entry.Owner
    };

    private Entry? Find(string wanted)
    {
        if (_entries.TryGetValue(wanted, out var byId)) return byId;
        if (!int.TryParse(wanted, out var pid)) return null;
        return _entries.Values.FirstOrDefault(entry => entry.ProcessId == pid);
    }

    private string NextId()
    {
        string id;
        do { id = "shell-" + (++_autoIds).ToString(); }
        while (_entries.ContainsKey(id));
        return id;
    }

    private static bool Same(string left, string? right) =>
        right == null || string.Equals(left, right, StringComparison.Ordinal);

    private static void Sweep(string parent)
    {
        try
        {
            var cutoff = DateTime.UtcNow - KeepLogs;
            foreach (var folder in Directory.EnumerateDirectories(parent))
            {
                try { if (Directory.GetLastWriteTimeUtc(folder) < cutoff) Directory.Delete(folder, true); }
                catch (Exception) { }
            }
        }
        catch (Exception) { }
    }

    private void TryDeleteFolder()
    {
        try { Directory.Delete(_folder, true); }
        catch (Exception) { }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception) { }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(BackgroundShells));
    }
}
