using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace LiteLLM.Core;

/// <summary>What the user's message asked for, resolved against the tools that are actually offered right now.
///
/// The token in the message is the whole selection: nothing is stored, so a retry, a reloaded conversation and a
/// copied prompt all select the same tools without a hidden flag to fall out of step. Explicit selection narrows the
/// MCP tools offered for the turn — it never approves anything. A name that resolves to nothing is a row and a
/// sentence in the prompt, not a silent drop; a name that resolves to two servers is a question for the user, not a
/// coin toss.</summary>
public sealed class McpToolSelection
{
    /// <summary>A turn can name more tools than are worth putting in one request. The setting's cap is also applied;
    /// this is the ceiling either way.</summary>
    public const int MaxSelectedTools = 32;

    /// <summary>How much the selection may add to the system prompt. The tool definitions themselves are capped
    /// separately by <see cref="McpToolSnapshot"/>.</summary>
    private const int MaxHintChars = 1600;

    private const int MaxListedTools = 12;
    private const int MaxListedProblems = 8;

    private readonly HashSet<string> _allowed;

    private McpToolSelection(bool narrows, bool suppressed, IReadOnlyList<McpToolInfo> selected,
        IReadOnlyList<ToolDefinition> definitions, string? hint, IReadOnlyList<McpTurnRow> rows)
    {
        Narrows = narrows;
        Suppressed = suppressed;
        Selected = selected;
        Definitions = definitions;
        Hint = hint;
        Rows = rows;
        _allowed = new HashSet<string>(selected.Select(tool => tool.ExposedName), StringComparer.Ordinal);
    }

    /// <summary>The message named no tool: the connected tools are offered, subject only to the automatic offer's
    /// own bound (<see cref="McpToolSnapshot.AutomaticDefinitions"/>), exactly as before this feature.</summary>
    public static McpToolSelection Automatic { get; } = new McpToolSelection(false, false,
        Array.Empty<McpToolInfo>(), Array.Empty<ToolDefinition>(), null, Array.Empty<McpTurnRow>());

    /// <summary>The turn offered no MCP tool at all because the message did not mention MCP.
    ///
    /// Every offered tool is a schema with a description in every request of every round, so a chat that never
    /// asks for one pays for the servers' payload before the model can start answering — which is a turn that
    /// looks slow for no visible reason. The prompt is told, so the model does not wonder why a tool it can see in
    /// the Dashboard is not in its hand; the user is told by the composer's MCP chip, which is where a state that
    /// lasts longer than one turn belongs — a row on every message would be the same sentence repeated.</summary>
    public static McpToolSelection WithoutTools() => new McpToolSelection(false, true,
        Array.Empty<McpToolInfo>(), Array.Empty<ToolDefinition>(),
        "MCP tools\nThis message did not ask for an MCP tool, so none are offered this turn. The MCP servers are " +
        "still connected, and a message that names a tool with # — or mentions MCP — offers them again. Do not " +
        "claim to have run an MCP tool, and do not describe what one would have returned.\n",
        Array.Empty<McpTurnRow>());

    /// <summary>True when at least one explicit mention resolved. Only then is the offer narrowed.</summary>
    public bool Narrows { get; }

    /// <summary>True when the message did not mention MCP and the turn therefore offers none of its tools.</summary>
    public bool Suppressed { get; }

    /// <summary>The definitions this turn must send instead of the live snapshot: the selected tools when the
    /// message narrowed, or an empty list when it said nothing about MCP. Null sends the snapshot, which is the
    /// turn that did not narrow and still offers every connected tool.</summary>
    public IReadOnlyList<ToolDefinition>? PinnedDefinitions => Narrows || Suppressed ? Definitions : null;

    public IReadOnlyList<McpToolInfo> Selected { get; }

    /// <summary>The definitions for exactly <see cref="Selected"/>, in snapshot order.</summary>
    public IReadOnlyList<ToolDefinition> Definitions { get; }

    /// <summary>The extension-authored block for the system prompt, or null when the message said nothing about
    /// tools. It lists the selected tools by their exposed names so the model does not have to guess.</summary>
    public string? Hint { get; }

    /// <summary>What the transcript should say about the selection: what was selected, what did not resolve, and
    /// whether the cap shortened it. Nothing here is silent.</summary>
    public IReadOnlyList<McpTurnRow> Rows { get; }

    /// <summary>Whether this turn may call the named MCP tool. A turn with no resolved selection allows everything,
    /// because a typo must not strip the user of every MCP tool; a turn whose message said nothing about MCP allows
    /// none, because none were offered and a call for one has to be answered rather than run.</summary>
    public bool Allows(string exposedName) =>
        !Suppressed && (!Narrows || (!string.IsNullOrEmpty(exposedName) && _allowed.Contains(exposedName)));

    /// <param name="offerWhenUnmentioned">False offers nothing to a message that says nothing about MCP. A message
    /// that mentions MCP in any shape — a <c>#</c> token, a backticked name, the word itself — takes the same path
    /// as before, so neither an unknown name nor a prose request can leave the turn with no tool at all.</param>
    public static McpToolSelection Resolve(McpToolSnapshot? snapshot, string? message,
        int maxTools = MaxSelectedTools, IReadOnlyCollection<string>? explained = null,
        bool offerWhenUnmentioned = true)
    {
        snapshot = snapshot ?? McpToolSnapshot.Empty;
        var mentions = McpToolMentions.Parse(message);
        // Nothing is suppressed when there is nothing to offer: the composer's MCP chip already answers "no server
        // is enabled", and a sentence about tools that do not exist would be a second, worse answer.
        if (mentions.Count == 0 && !offerWhenUnmentioned && snapshot.Tools.Count > 0 &&
            !McpToolMentions.SaysMcp(message))
            return WithoutTools();
        if (mentions.Count == 0) return Automatic;

        var limit = Math.Max(1, Math.Min(maxTools <= 0 ? MaxSelectedTools : maxTools, MaxSelectedTools));
        var selected = new List<McpToolInfo>();
        var chosen = new HashSet<string>(StringComparer.Ordinal);
        var problems = new List<MentionProblem>();
        var hinted = new List<McpToolInfo>();
        var hintedNames = new HashSet<string>(StringComparer.Ordinal);
        var anyExplicit = false;
        var capped = false;

        foreach (var mention in mentions)
        {
            // A server the user configured but this turn cannot offer is answered by McpDisabledMentions with a
            // sentence that names it. Reporting it here as well would put two rows on the same word.
            if (explained != null && explained.Contains(mention.Text)) continue;
            var matches = ResolveToken(snapshot, mention.Text);

            if (mention.Kind == McpMentionKind.Hint)
            {
                // A backticked name is prose. It may inform the model but it never narrows the turn, and an unknown
                // one is ignored rather than reported — it is indistinguishable from a code symbol the user quoted.
                if (matches.Count == 1)
                {
                    if (hintedNames.Add(matches[0].ExposedName)) hinted.Add(matches[0]);
                }
                else if (matches.Count > 1)
                {
                    problems.Add(new MentionProblem(mention, matches));
                }
                continue;
            }

            anyExplicit = true;
            if (matches.Count == 1)
            {
                var tool = matches[0];
                if (!chosen.Add(tool.ExposedName)) continue;
                if (selected.Count >= limit) { capped = true; continue; }
                selected.Add(tool);
            }
            else
            {
                problems.Add(new MentionProblem(mention, matches));
            }
        }

        // Snapshot order, so the request's tool list is the same for the same message whatever order the tokens
        // appeared in.
        var order = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < snapshot.Tools.Count; index++) order[snapshot.Tools[index].ExposedName] = index;
        selected = selected
            .OrderBy(tool => order.TryGetValue(tool.ExposedName, out var position) ? position : int.MaxValue)
            .ToList();

        var narrows = selected.Count > 0;
        var definitions = narrows
            ? snapshot.DefinitionsFor(selected.Select(tool => tool.ExposedName))
            : (IReadOnlyList<ToolDefinition>)Array.Empty<ToolDefinition>();

        var rows = new List<McpTurnRow>();
        if (narrows)
            rows.Add(new McpTurnRow
            {
                Summary = "MCP · " + selected.Count + " selected tool" + (selected.Count == 1 ? "" : "s") +
                          " offered this turn",
                Detail = string.Join("\n", selected.Take(MaxListedTools).Select(tool =>
                    "· " + Describe(tool) + " → " + McpToolMentions.TokenFor(tool)))
            });
        foreach (var problem in problems.Take(MaxListedProblems)) rows.Add(problem.ToRow());
        if (capped)
            rows.Add(new McpTurnRow
            {
                Summary = "MCP · only the first " + limit + " selected tools were offered this turn",
                Ok = false,
                Detail = "The per-turn cap limited the explicit selection. Remove a tool from the message or raise " +
                         "the cap in Tools ▸ Options ▸ LiteLLM ▸ MCP."
            });

        return new McpToolSelection(narrows, false, selected, definitions,
            BuildHint(narrows, anyExplicit, selected, problems, hinted, capped, limit), rows);
    }

    /// <summary>Resolution precedence, exact first and fuzzy never: exposed name, raw tool name, server-qualified
    /// name, then the same three case-insensitively. More than one survivor is ambiguous, which is answered by a
    /// question rather than by picking the first.</summary>
    private static List<McpToolInfo> ResolveToken(McpToolSnapshot snapshot, string token)
    {
        var tools = snapshot.Tools;
        var results = new List<McpToolInfo>();
        if (string.IsNullOrEmpty(token)) return results;

        foreach (var tool in tools)
            if (string.Equals(tool.ExposedName, token, StringComparison.Ordinal)) results.Add(tool);
        if (results.Count > 0) return results;

        // A tool whose own name contains dots wins as a whole before the name is split at a server boundary.
        foreach (var tool in tools)
            if (string.Equals(tool.Name, token, StringComparison.Ordinal)) results.Add(tool);
        if (results.Count > 0) return results;

        // Server-qualified: every dot or slash is a possible boundary, so a server name or a tool name may itself
        // contain dots. Both separators are accepted because a token may be written by hand; the picker inserts the
        // dot form. The leftmost boundary that yields a match wins.
        for (var at = 1; at < token.Length; at++)
        {
            if (token[at] != '.' && token[at] != '/') continue;
            var prefix = token.Substring(0, at);
            var suffix = token.Substring(at + 1);
            var exact = new List<McpToolInfo>();
            var loose = new List<McpToolInfo>();
            foreach (var tool in tools)
            {
                if (!ServerMatches(tool.Server, prefix)) continue;
                if (string.Equals(tool.Name, suffix, StringComparison.Ordinal)) exact.Add(tool);
                else if (string.Equals(tool.Name, suffix, StringComparison.OrdinalIgnoreCase)) loose.Add(tool);
            }
            if (exact.Count > 0) return exact;
            if (loose.Count > 0) return loose;
        }

        foreach (var tool in tools)
            if (string.Equals(tool.ExposedName, token, StringComparison.OrdinalIgnoreCase)) results.Add(tool);
        if (results.Count > 0) return results;

        foreach (var tool in tools)
            if (string.Equals(tool.Name, token, StringComparison.OrdinalIgnoreCase)) results.Add(tool);
        return results;
    }

    private static bool ServerMatches(McpServerConfiguration? server, string prefix) =>
        string.Equals(server?.Name, prefix, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(McpToolNaming.Slug(server?.Name), prefix, StringComparison.OrdinalIgnoreCase);

    private static string Describe(McpToolInfo tool) =>
        ChatStateLimits.OneLine(tool.Server?.Name, 60) + " · " + ChatStateLimits.OneLine(tool.Name, 60);

    /// <summary>The prompt block. It is written by the extension, so it may state rules; the server-authored names
    /// in it are one-lined and bounded before they go in.</summary>
    private static string? BuildHint(bool narrows, bool anyExplicit, IReadOnlyList<McpToolInfo> selected,
        IReadOnlyList<MentionProblem> problems, IReadOnlyList<McpToolInfo> hinted, bool capped, int limit)
    {
        if (!narrows && !anyExplicit && problems.Count == 0 && hinted.Count == 0) return null;

        var text = new StringBuilder();
        if (narrows)
        {
            text.Append("MCP tools selected for this turn\n");
            text.Append("The user's current message selected the MCP tools below. Only these MCP tools are offered " +
                        "this turn; the built-in workspace tools are unchanged. Do not call any other MCP tool - a " +
                        "call to one that is not listed is refused before it runs.\n");
            foreach (var tool in selected.Take(MaxListedTools))
                text.Append("- ").Append(Describe(tool)).Append(" - call it as ")
                    .Append(tool.ExposedName).Append('\n');
            if (selected.Count > MaxListedTools)
                text.Append("- and ").Append(selected.Count - MaxListedTools)
                    .Append(" more, all present in the tool definitions.\n");
            text.Append("Selection is not approval: every call still follows the approval rules, and a declined call " +
                        "must be reported as declined. A tool result in this conversation is the only proof that a " +
                        "call ran.\n");
        }
        else if (anyExplicit)
        {
            // A name that resolves is always offered — the snapshot holds every tool the servers listed and the
            // automatic cap never removes one from it — so the only way to reach this branch is a name no connected
            // server offers. Saying it might be past a cap would send the model looking for a knob instead of
            // answering the user.
            text.Append("MCP tools\nThe user's current message named an MCP tool that is not offered. The MCP tools " +
                        "offered this turn remain available. Say plainly that the named tool is unavailable; do not " +
                        "substitute a different tool, and do not claim one ran.\n");
        }

        var unknown = problems.Where(problem => !problem.Ambiguous).ToList();
        if (unknown.Count > 0)
        {
            text.Append("These names match no offered MCP tool: ")
                .Append(string.Join(", ", unknown.Take(MaxListedProblems).Select(problem => problem.Display)))
                .Append(". Say they are unavailable rather than calling something else in their place.\n");
        }

        var ambiguous = problems.Where(problem => problem.Ambiguous).Take(MaxListedProblems).ToList();
        foreach (var problem in ambiguous)
        {
            text.Append(problem.Display).Append(" matches more than one MCP tool (")
                .Append(string.Join(", ", problem.Matches.Take(6).Select(Describe)))
                .Append("). Ask which server the user means, or use a server-qualified name, before calling one.\n");
        }

        if (hinted.Count > 0)
        {
            text.Append("The message also mentions in backticks: ")
                .Append(string.Join(", ", hinted.Take(MaxListedProblems).Select(tool => tool.ExposedName)))
                .Append(". Those mentions are hints, not a selection; call one only if it fits the request.\n");
        }
        if (capped)
            text.Append("Only the first ").Append(limit)
                .Append(" explicitly selected tools fit the per-turn cap and were offered.\n");

        var hint = text.ToString().TrimEnd();
        return hint.Length <= MaxHintChars ? hint : hint.Substring(0, MaxHintChars - 1) + "…";
    }

    private sealed class MentionProblem
    {
        internal MentionProblem(McpToolMention mention, List<McpToolInfo> matches)
        {
            Mention = mention;
            Matches = matches;
        }

        public McpToolMention Mention { get; }
        public List<McpToolInfo> Matches { get; }
        public bool Ambiguous => Matches.Count > 1;

        /// <summary>How the token is shown back: the <c>#</c> the user typed, or the backticks they wrote it in.</summary>
        public string Display => Mention.Kind == McpMentionKind.Hint
            ? "`" + ChatStateLimits.OneLine(Mention.Text, 80) + "`"
            : "#" + ChatStateLimits.OneLine(Mention.Text, 80);

        internal McpTurnRow ToRow() => Ambiguous
            ? new McpTurnRow
            {
                Summary = "MCP · " + Display + " matches " + Matches.Count + " tools; none selected",
                Ok = false,
                Detail = string.Join("\n", Matches.Take(MaxListedProblems)
                    .Select(tool => "· " + Describe(tool) + " → " + McpToolMentions.TokenFor(tool)))
            }
            : new McpTurnRow
            {
                Summary = "MCP · no offered tool matches " + Display,
                Ok = false
            };
    }
}
