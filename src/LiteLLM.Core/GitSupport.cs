using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace LiteLLM.Core;

public sealed class GitRemoteInfo
{
    internal GitRemoteInfo(string original, string protocol, string host)
    {
        Original = original;
        Protocol = protocol;
        Host = host;
    }

    public string Original { get; }
    public string Protocol { get; }
    public string Host { get; }
}

/// <summary>Shell-free helpers for invoking Git and handling remote information safely.</summary>
public static class GitSupport
{
    private static readonly Regex SchemeRemote = new Regex(
        @"^(?<scheme>https?|ssh|git)://(?:(?<userinfo>[^/@]+)@)?(?<host>\[[^\]]+\]|[^/:?#]+)(?::\d+)?(?:[/?#]|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ScpRemote = new Regex(
        @"^(?:(?<user>[^@/:\s]+)@)?(?<host>\[[^\]]+\]|[^/:\s]+):(?<path>.+)$",
        RegexOptions.CultureInvariant);
    private static readonly Regex UrlCredentials = new Regex(
        @"(?<scheme>https?|ssh|git)://(?<credentials>[^/@\s]+)@",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Builds a Windows CreateProcess argument string with one safely quoted token per item.</summary>
    public static string BuildCommandLine(IReadOnlyList<string> arguments)
    {
        if (arguments == null) throw new ArgumentNullException(nameof(arguments));
        var result = new StringBuilder();
        for (var i = 0; i < arguments.Count; i++)
        {
            if (arguments[i] == null) throw new ArgumentException("Arguments cannot contain null values.", nameof(arguments));
            if (i != 0) result.Append(' ');
            AppendQuotedArgument(result, arguments[i]);
        }
        return result.ToString();
    }

    /// <summary>Parses the legacy Windows command-line representation accepted by ProcessStartInfo.Arguments.</summary>
    public static IReadOnlyList<string> ParseCommandLine(string commandLine)
    {
        if (commandLine == null) throw new ArgumentNullException(nameof(commandLine));
        var result = new List<string>();
        var i = 0;
        while (true)
        {
            while (i < commandLine.Length && char.IsWhiteSpace(commandLine[i])) i++;
            if (i == commandLine.Length) break;
            var value = new StringBuilder();
            var quoted = false;
            while (i < commandLine.Length)
            {
                var slashes = 0;
                while (i < commandLine.Length && commandLine[i] == '\\') { slashes++; i++; }
                if (i < commandLine.Length && commandLine[i] == '"')
                {
                    value.Append('\\', slashes / 2);
                    if ((slashes & 1) != 0) { value.Append('"'); i++; }
                    else if (quoted && i + 1 < commandLine.Length && commandLine[i + 1] == '"') { value.Append('"'); i += 2; }
                    else { quoted = !quoted; i++; }
                    continue;
                }
                value.Append('\\', slashes);
                if (i == commandLine.Length || (!quoted && char.IsWhiteSpace(commandLine[i]))) break;
                value.Append(commandLine[i++]);
            }
            result.Add(value.ToString());
        }
        return result;
    }

    public static bool TryParseRemote(string value, out GitRemoteInfo remote)
    {
        remote = null;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var match = SchemeRemote.Match(value);
        if (match.Success)
        {
            remote = new GitRemoteInfo(value, match.Groups["scheme"].Value.ToLowerInvariant(), TrimIpv6Brackets(match.Groups["host"].Value));
            return true;
        }
        if (Regex.IsMatch(value, @"^[A-Za-z]:[\\/]")) return false;
        match = ScpRemote.Match(value);
        if (!match.Success || match.Groups["path"].Value.StartsWith("/", StringComparison.Ordinal)
            || match.Groups["path"].Value.StartsWith("\\", StringComparison.Ordinal)) return false;
        remote = new GitRemoteInfo(value, "ssh", TrimIpv6Brackets(match.Groups["host"].Value));
        return true;
    }

    /// <summary>Recognizes explicit credential failures, not authorization, connectivity, or repository errors.</summary>
    public static bool IsAuthenticationFailure(string output)
    {
        if (string.IsNullOrEmpty(output)) return false;
        return Contains(output, "authentication failed")
            || Contains(output, "could not read Username")
            || Contains(output, "terminal prompts disabled")
            || Contains(output, "Permission denied (publickey)")
            || Contains(output, "HTTP Basic: Access denied")
            || Contains(output, "Invalid username or password")
            || Contains(output, "Invalid credentials");
    }

    /// <summary>Removes user-info from supported URLs before text is logged or displayed.</summary>
    public static string RedactCredentials(string text)
    {
        if (text == null) throw new ArgumentNullException(nameof(text));
        return UrlCredentials.Replace(text, m => m.Groups["scheme"].Value + "://***@");
    }

    /// <summary>Returns the remote explicitly supplied to push, pull, or fetch; otherwise null.</summary>
    public static string GetExplicitRemote(IReadOnlyList<string> arguments)
    {
        if (arguments == null) throw new ArgumentNullException(nameof(arguments));
        if (arguments.Count == 0) return null;
        var commandIndex = FindCommand(arguments);
        if (commandIndex < 0) return null;
        var command = arguments[commandIndex];
        if (!EqualsIgnoreCase(command, "push") && !EqualsIgnoreCase(command, "pull") && !EqualsIgnoreCase(command, "fetch")) return null;
        for (var i = commandIndex + 1; i < arguments.Count; i++)
        {
            var arg = arguments[i];
            if (arg == "--") return i + 1 < arguments.Count ? arguments[i + 1] : null;
            if (arg == "--repo") return i + 1 < arguments.Count ? arguments[i + 1] : null;
            if (arg.StartsWith("--repo=", StringComparison.Ordinal)) return arg.Substring("--repo=".Length);
            if (arg.StartsWith("-", StringComparison.Ordinal))
            {
                if (OptionConsumesValue(arg) && arg.IndexOf('=') < 0) i++;
                continue;
            }
            return arg;
        }
        return null;
    }

    public static string GetOperation(IReadOnlyList<string> arguments)
    {
        if (arguments == null) throw new ArgumentNullException(nameof(arguments));
        var index = FindCommand(arguments);
        return index < 0 ? "" : arguments[index].ToLowerInvariant();
    }

    public static void ValidateArguments(IReadOnlyList<string> arguments)
    {
        if (arguments == null) throw new ArgumentNullException(nameof(arguments));
        foreach (var argument in arguments)
            if (argument == null || argument.IndexOf('\0') >= 0 || argument.IndexOf('\r') >= 0 || argument.IndexOf('\n') >= 0)
                throw new ArgumentException("Git arguments cannot contain NUL, CR, or LF characters.", nameof(arguments));
    }

    private static int FindCommand(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg == "-C" || arg == "-c" || arg == "--git-dir" || arg == "--work-tree" || arg == "--namespace" || arg == "--exec-path") { i++; continue; }
            if (arg.StartsWith("--git-dir=", StringComparison.Ordinal) || arg.StartsWith("--work-tree=", StringComparison.Ordinal) || arg.StartsWith("--namespace=", StringComparison.Ordinal) || arg.StartsWith("--exec-path=", StringComparison.Ordinal)) continue;
            if (!arg.StartsWith("-", StringComparison.Ordinal)) return i;
        }
        return -1;
    }

    private static bool OptionConsumesValue(string option)
    {
        return option == "--repo" || option == "--receive-pack" || option == "--upload-pack"
            || option == "--exec" || option == "--depth" || option == "--deepen" || option == "--shallow-since"
            || option == "--shallow-exclude" || option == "--negotiation-tip" || option == "--server-option"
            || option == "-o" || option == "-j";
    }

    private static void AppendQuotedArgument(StringBuilder result, string argument)
    {
        if (argument.Length != 0 && argument.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0) { result.Append(argument); return; }
        result.Append('"');
        var slashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') { result.Append('\\', slashes * 2 + 1).Append('"'); slashes = 0; continue; }
            result.Append('\\', slashes).Append(c); slashes = 0;
        }
        result.Append('\\', slashes * 2).Append('"');
    }

    private static bool Contains(string text, string value) => text.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
    private static bool EqualsIgnoreCase(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static string TrimIpv6Brackets(string host) => host.Length > 1 && host[0] == '[' && host[host.Length - 1] == ']' ? host.Substring(1, host.Length - 2) : host;
}
