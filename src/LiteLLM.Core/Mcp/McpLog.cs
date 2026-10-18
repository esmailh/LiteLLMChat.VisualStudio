using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace LiteLLM.Core;

public sealed class McpLogEntry
{
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public string Level { get; set; } = "info";
    public string Message { get; set; } = "";
}

/// <summary>A bounded per-server log.
///
/// MCP servers write to stderr freely and a long-running one writes a lot of it, so this keeps only the most recent
/// entries and never grows with the turn. Values that look like credentials are masked on the way in: the server's
/// own diagnostics are the one place a token the extension passed in a header can come back out, and a log panel is
/// read and pasted like any other text.</summary>
public sealed class McpLog
{
    public const int DefaultCapacity = 500;
    private const int MaxMessageLength = 2000;

    // "Authorization: Bearer x", "TOKEN=abc" and the like. The value is masked, the name is kept so the line still
    // says what happened.
    private static readonly Regex Assignment = new Regex(
        @"(?<name>[A-Za-z0-9_.\-]*(?:token|key|secret|password|passwd|auth|cookie|credential)[A-Za-z0-9_.\-]*)\s*[:=]\s*(?:Bearer\s+)?(?<value>[^\s,;""']+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Bearer = new Regex(@"Bearer\s+[A-Za-z0-9._~+/=\-]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly int _capacity;
    private readonly Queue<McpLogEntry> _entries;
    private readonly object _gate = new object();

    public McpLog(int capacity = DefaultCapacity)
    {
        _capacity = capacity > 0 ? capacity : DefaultCapacity;
        _entries = new Queue<McpLogEntry>(_capacity);
    }

    public event Action<McpLogEntry>? Added;

    public void Add(string level, string message)
    {
        var entry = new McpLogEntry
        {
            Level = string.IsNullOrWhiteSpace(level) ? "info" : level.Trim(),
            Message = Redact(Cap(message ?? ""))
        };
        lock (_gate)
        {
            while (_entries.Count >= _capacity) _entries.Dequeue();
            _entries.Enqueue(entry);
        }
        try { Added?.Invoke(entry); }
        catch { /* A listener that throws must not take the session's receive loop down with it. */ }
    }

    public void Debug(string message) => Add("debug", message);
    public void Info(string message) => Add("info", message);
    public void Warning(string message) => Add("warning", message);
    public void Error(string message) => Add("error", message);

    public IReadOnlyList<McpLogEntry> Snapshot()
    {
        lock (_gate) return new List<McpLogEntry>(_entries);
    }

    private static string Cap(string value) =>
        value.Length <= MaxMessageLength ? value : value.Substring(0, MaxMessageLength) + "…";

    public static string Redact(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var masked = Assignment.Replace(value, match => match.Groups["name"].Value + ": <redacted>");
        return Bearer.Replace(masked, "Bearer <redacted>");
    }
}
