using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace LiteLLM.Core;

public sealed class McpToolRef
{
    public McpServerConfiguration Server { get; set; } = new McpServerConfiguration();
    /// <summary>The name the server lists, which is what the UI and the log use.</summary>
    public string OriginalName { get; set; } = "";
    /// <summary>The name the model sees and calls.</summary>
    public string ExposedName { get; set; } = "";
}

/// <summary>Maps a server's tool name onto the OpenAI function-name alphabet.
///
/// The model is given one flat list, so two servers may both offer <c>search</c>. The exposed name therefore carries
/// the server, is sanitized to <c>[A-Za-z0-9_-]</c>, and is capped at the 64 characters the function API allows.
/// The reverse map built here is the only thing dispatch trusts.</summary>
public static class McpToolNaming
{
    public const int MaxLength = 64;
    private static readonly Regex Invalid = new Regex("[^A-Za-z0-9_-]", RegexOptions.Compiled);

    public static string Slug(string? value)
    {
        var slug = Invalid.Replace(value ?? "", "_").Trim('_');
        return slug.Length == 0 ? "tool" : slug;
    }

    public static string Exposed(string? serverName, string? toolName)
    {
        var server = Slug(serverName);
        var tool = Slug(toolName);
        if (server.Length > 24) server = server.Substring(0, 24);
        var prefix = "mcp__" + server + "__";
        if (prefix.Length + tool.Length <= MaxLength) return prefix + tool;
        // Cut the tool, not the server: two servers offering long names must still be distinguishable at a glance.
        var hash = Hash8(server + "\u0000" + tool);
        var room = Math.Max(1, MaxLength - prefix.Length - hash.Length - 1);
        return prefix + tool.Substring(0, room) + "_" + hash;
    }

    /// <summary>Assigns unique exposed names in the order the input arrives. A duplicate — the same server listing
    /// the same tool twice, or two sanitized names that collide — gets a numeric suffix rather than overwriting the
    /// first, because the reverse map must remain one-to-one.</summary>
    public static IReadOnlyList<McpToolRef> Assign(IEnumerable<(McpServerConfiguration Server, string ToolName)> tools)
    {
        var result = new List<McpToolRef>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (server, toolName) in tools)
        {
            var exposed = Exposed(server.Name, toolName);
            if (used.Contains(exposed))
            {
                var suffix = 2;
                string candidate;
                do
                {
                    var text = "_" + suffix.ToString(CultureInfo.InvariantCulture);
                    candidate = exposed.Length + text.Length <= MaxLength
                        ? exposed + text
                        : exposed.Substring(0, MaxLength - text.Length) + text;
                    suffix++;
                } while (used.Contains(candidate));
                exposed = candidate;
            }
            used.Add(exposed);
            result.Add(new McpToolRef { Server = server, OriginalName = toolName, ExposedName = exposed });
        }
        return result;
    }

    private static string Hash8(string value)
    {
        using (var sha = SHA256.Create())
        {
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
            return BitConverter.ToString(hash, 0, 4).Replace("-", "").ToLowerInvariant();
        }
    }
}
