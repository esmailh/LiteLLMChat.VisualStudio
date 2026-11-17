using System;
using System.Collections.Generic;
using System.Text;

namespace LiteLLM.Core;

/// <summary>The two sentences a background shell produces, kept out of the tool and out of the window so that both
/// say exactly the same thing.
///
/// The start result is what the model reads instead of an exit code, so it has to carry everything the model needs
/// to use the shell it just started: the id, the process id, the file its output goes to, and the one thing a file
/// cannot say for itself — that there may be more output later. The completion block is written in the shape a
/// tool result has, because that is what the model recognises as a fact about its own work rather than as a
/// remark from the user.</summary>
public static class ShellNotice
{
    public static string Started(ShellStart start)
    {
        var text = new StringBuilder();
        text.Append("Started ").Append(start.Id).Append(" (pid ").Append(start.ProcessId).Append(", ").Append(start.Shell)
            .Append(") in ").Append(start.WorkingDirectory).Append(". It is still running.\n");
        text.Append("Output goes to ").Append(start.LogPath)
            .Append(" — read it with read_file, which returns the last lines of a log unless start_line is given, ")
            .Append("so check the end for whether it is ready and for what it printed.\n");
        text.Append("The shell outlives this call and the turn: it is stopped when the session ends, by \"taskkill /PID ")
            .Append(start.ProcessId).Append(" /T /F\", or by itself. You are told when it ends, and its log stays readable after that.");
        return text.ToString();
    }

    /// <summary>One ended shell, as the model is told about it: attributes rather than prose, so that a reader can
    /// take the id and the exit code apart from the sentence around them.</summary>
    public static string Completed(ShellCompletion completion)
    {
        var text = new StringBuilder();
        text.Append("<shell id=\"").Append(Escape(completion.Id)).Append('"')
            .Append(" pid=\"").Append(completion.ProcessId).Append('"')
            .Append(" shell=\"").Append(completion.Shell).Append('"')
            .Append(" state=\"completed\"")
            .Append(" exit=\"").Append(completion.ExitCode?.ToString() ?? "unknown").Append('"')
            .Append(" after=\"").Append(Seconds(completion.Duration)).Append("s\"")
            .Append(" log=\"").Append(Escape(completion.LogPath)).Append("\" />");
        text.Append(' ').Append(completion.Id).Append(" has ended; read its log with read_file to see its last output.");
        return text.ToString();
    }

    /// <summary>The block for the system prompt. One <c>&lt;shells&gt;</c> element rather than a paragraph so that a
    /// block that is empty renders as nothing at all, and so that the model can tell these apart from the
    /// conversation: nothing here was said by the user.</summary>
    public static string Block(IReadOnlyList<ShellCompletion>? completions)
    {
        if (completions == null || completions.Count == 0) return "";
        var text = new StringBuilder("<shells>\n");
        foreach (var completion in completions) text.Append(Completed(completion)).Append('\n');
        text.Append("</shells>");
        return text.ToString();
    }

    private static string Seconds(TimeSpan duration) => duration.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);

    private static string Escape(string value) => value.Replace("&", "&amp;").Replace("\"", "&quot;").Replace("<", "&lt;").Replace(">", "&gt;");
}
