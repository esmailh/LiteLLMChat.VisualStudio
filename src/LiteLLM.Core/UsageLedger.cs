using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace LiteLLM.Core;

/// <summary>What one model has cost on this machine, summed over every turn that reported a usage object.</summary>
public sealed class UsageLedgerEntry
{
    /// <summary>The model's <see cref="ModelInfo.Key"/>: the server id and the model id together. The pair is what
    /// the dashboard row has, and two servers may serve the same model id under different prices.</summary>
    public string ModelKey { get; set; } = "";
    public long PromptTokens { get; set; }
    public long CompletionTokens { get; set; }
    public long TotalTokens { get; set; }
    public int Turns { get; set; }
    public DateTime FirstUtc { get; set; }
    public DateTime LastUtc { get; set; }

    /// <summary>The count as it fits in a list row beside the model name.</summary>
    [JsonIgnore]
    public string Short => UsageFormat.Short(TotalTokens) + " tokens";

    /// <summary>The exact numbers, shown when the row is hovered. The date is local and invariant: it is read by the
    /// person in front of the machine, not written to anything.</summary>
    [JsonIgnore]
    public string Detail =>
        UsageFormat.Count(TotalTokens) + " tokens · "
        + UsageFormat.Count(PromptTokens) + " in / " + UsageFormat.Count(CompletionTokens) + " out · "
        + Turns.ToString(CultureInfo.InvariantCulture) + (Turns == 1 ? " turn" : " turns")
        + (LastUtc.Year > 1
            ? " · last used " + LastUtc.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : "");
}

/// <summary>The ledger file on disk. Kept as its own type so the serializer has one shape to read and write, and
/// the seeded marker travels with the entries it describes.</summary>
public sealed class UsageLedgerDocument
{
    public int Version { get; set; } = 1;
    /// <summary>When the stored conversations were scanned for usage the ledger had not recorded. Null means the
    /// scan has never happened; any value means it must not happen again, or every launch would add the same turns
    /// a second time.</summary>
    public DateTime? SeededUtc { get; set; }
    public List<UsageLedgerEntry> Entries { get; set; } = new List<UsageLedgerEntry>();
}

/// <summary>A durable per-model total of server-reported token usage.
///
/// The conversations cannot answer "how much has this model used": they are solution-scoped, capped at
/// <see cref="ChatStateLimits.MaxSessions"/> and <see cref="ChatStateLimits.MaxTurnsPerSession"/>, and deleting a
/// chat deletes its usage with it — a total that shrank when a conversation was removed would not be a total. So
/// each turn adds to this file instead, which is global to the Windows user because a model is configured once in
/// the Dashboard for the whole IDE, not once per solution.
///
/// A provider that reports no usage contributes nothing. It is not a zero: the count is a floor, and the Dashboard
/// says so rather than pretending the missing turns were free.</summary>
public sealed class UsageLedger
{
    /// <summary>Enough for every model a person is likely to keep configured, with room for renamed or retired
    /// ones. Past it the oldest entry goes, because a model nobody has called in a year is not what the row is for.</summary>
    public const int MaxEntries = 200;

    private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
    {
        MissingMemberHandling = MissingMemberHandling.Ignore,
        NullValueHandling = NullValueHandling.Ignore
    };

    private readonly string _path;
    private readonly UsageLedgerDocument _document;

    public UsageLedger(string? path = null)
    {
        _path = path ?? DefaultPath;
        _document = Load(_path);
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiteLLM.VisualStudio", "token-usage.json");

    public IReadOnlyList<UsageLedgerEntry> Entries => _document.Entries;

    public bool Any => TotalTokens > 0;

    public long PromptTokens => Sum(e => e.PromptTokens);
    public long CompletionTokens => Sum(e => e.CompletionTokens);
    public long TotalTokens => Sum(e => e.TotalTokens);

    public UsageLedgerEntry? For(string modelKey) =>
        _document.Entries.FirstOrDefault(e => string.Equals(e.ModelKey, modelKey, StringComparison.Ordinal));

    /// <summary>The line under the Dashboard's model list. The input/output split is there because two numbers
    /// that large are hard to read as one, and the sentence names the source: these are the server's counts, not a
    /// local estimate.</summary>
    public string Summary => TotalTokens <= 0
        ? "No server-reported token usage recorded yet"
        : "Used so far · " + UsageFormat.Count(TotalTokens) + " tokens · "
          + UsageFormat.Count(PromptTokens) + " in / " + UsageFormat.Count(CompletionTokens) + " out";

    /// <summary>Adds one turn's spending to its model. A turn with no reported usage adds nothing at all, so an
    /// empty response can never be recorded as a free one.</summary>
    public void Record(string modelKey, TokenUsage? usage, DateTime utc)
    {
        if (string.IsNullOrWhiteSpace(modelKey) || usage == null || !usage.Any) return;
        var entry = For(modelKey);
        if (entry == null)
        {
            entry = new UsageLedgerEntry { ModelKey = modelKey, FirstUtc = utc };
            _document.Entries.Add(entry);
        }
        entry.PromptTokens = Add(entry.PromptTokens, usage.PromptTokens);
        entry.CompletionTokens = Add(entry.CompletionTokens, usage.CompletionTokens);
        entry.TotalTokens = Add(entry.TotalTokens, usage.TotalTokens > 0 ? usage.TotalTokens : Add(usage.PromptTokens, usage.CompletionTokens));
        entry.Turns = entry.Turns == int.MaxValue ? int.MaxValue : entry.Turns + 1;
        if (entry.FirstUtc.Year <= 1) entry.FirstUtc = utc;
        if (utc > entry.LastUtc) entry.LastUtc = utc;
        Trim();
    }

    /// <summary>Fills the ledger once from the conversations already on disk, so installing this build does not
    /// erase the usage those conversations were already keeping. Returns true when the document changed and needs
    /// saving — including the first call against an empty history, whose only change is the seeded marker.</summary>
    public bool Seed(IEnumerable<ChatSessionState>? sessions)
    {
        if (_document.SeededUtc != null) return false;
        foreach (var session in sessions ?? Enumerable.Empty<ChatSessionState>())
        {
            if (session?.Turns == null) continue;
            foreach (var turn in session.Turns)
            {
                if (turn?.Usage == null || string.IsNullOrWhiteSpace(turn.ModelKey)) continue;
                // A stored turn without a start time is still spending that happened; the ledger only uses the
                // date as a tie-breaker for trimming, so now is better than the year 1.
                Record(turn.ModelKey, turn.Usage, turn.StartedUtc.Year > 1 ? turn.StartedUtc : DateTime.UtcNow);
            }
        }
        _document.SeededUtc = DateTime.UtcNow;
        return true;
    }

    public void Save()
    {
        Trim();
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonConvert.SerializeObject(_document, Formatting.Indented, Settings), Encoding.UTF8);
        if (File.Exists(_path)) File.Replace(temporary, _path, null); else File.Move(temporary, _path);
    }

    private void Trim()
    {
        if (_document.Entries.Count <= MaxEntries) return;
        _document.Entries = _document.Entries
            .OrderByDescending(e => e.LastUtc)
            .Take(MaxEntries)
            .OrderBy(e => e.FirstUtc)
            .ToList();
    }

    private long Sum(Func<UsageLedgerEntry, long> value)
    {
        long total = 0;
        foreach (var entry in _document.Entries) total = Add(total, value(entry));
        return total;
    }

    private static UsageLedgerDocument Load(string path)
    {
        if (!File.Exists(path)) return new UsageLedgerDocument();
        try
        {
            var document = JsonConvert.DeserializeObject<UsageLedgerDocument>(File.ReadAllText(path), Settings)
                           ?? new UsageLedgerDocument();
            Normalize(document);
            return document;
        }
        catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException)
        {
            // A damaged ledger costs the totals, not the turn that is running: the chat and the build check do not
            // depend on this file. It starts empty and is seeded again from the stored conversations.
            return new UsageLedgerDocument();
        }
    }

    private static void Normalize(UsageLedgerDocument document)
    {
        document.Version = Math.Max(1, document.Version);
        document.Entries ??= new List<UsageLedgerEntry>();
        document.Entries = document.Entries
            .Where(e => e != null && !string.IsNullOrWhiteSpace(e.ModelKey))
            .ToList();
        foreach (var entry in document.Entries)
        {
            entry.PromptTokens = Math.Max(0, entry.PromptTokens);
            entry.CompletionTokens = Math.Max(0, entry.CompletionTokens);
            entry.TotalTokens = entry.TotalTokens > 0
                ? entry.TotalTokens
                : Add(entry.PromptTokens, entry.CompletionTokens);
            entry.Turns = Math.Max(0, entry.Turns);
        }
    }

    /// <summary>A total of two counts cannot wrap into a smaller one on screen. The values are far below these
    /// bounds in practice; the arithmetic costs nothing and the alternative is a negative total nobody can explain.</summary>
    private static long Add(long left, long right)
    {
        if (right <= 0) return Math.Max(0, left);
        if (left <= 0) return right;
        return left > long.MaxValue - right ? long.MaxValue : left + right;
    }
}
