using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LiteLLM.Core;

/// <summary>What a tool call becomes in the transcript: the text the model reads, the row's summary, and what had to
/// be left out. The model is text-only, so binary content is written to a file and named here — never dropped
/// silently and never pasted into the conversation.</summary>
public sealed class McpFormattedResult
{
    public string Content { get; set; } = "";
    public string Summary { get; set; } = "";
    public bool Ok { get; set; } = true;
    /// <summary>Binary blocks written to disk.</summary>
    public int Attachments { get; set; }
    /// <summary>Binary blocks that could not be written; the content says why.</summary>
    public int Unsaved { get; set; }
    public bool Truncated { get; set; }
}

/// <summary>Converts a <c>tools/call</c> result into text and a one-line summary.
///
/// Every cap here reports itself: text over <see cref="MaxBlockChars"/> is clipped and the summary says so, and the
/// overall body stops at <see cref="MaxTotalChars"/>. Binary content is saved through <c>saveMedia</c>, which the
/// tests replace; the default writes under <c>%LOCALAPPDATA%</c> and never into the solution.</summary>
public sealed class McpResultFormatter
{
    public const int MaxBlockChars = 16000;
    public const int MaxTotalChars = 40000;
    public const int MaxBlobBytes = 4 * 1024 * 1024;
    private const int MarkerReserve = 24;

    private readonly Func<string, byte[], string> _saveMedia;

    public McpResultFormatter(Func<string, byte[], string>? saveMedia = null)
    {
        _saveMedia = saveMedia ?? SaveUnderLocalAppData;
    }

    public McpFormattedResult Format(McpToolInfo tool, JObject result)
    {
        var serverName = tool.Server?.Name ?? "mcp";
        var builder = new StringBuilder();
        var truncated = false;
        var attachments = 0;
        var unsaved = 0;

        void Append(string text)
        {
            if (text.Length == 0) return;
            var room = MaxTotalChars - MarkerReserve - builder.Length;
            if (room <= 0) { truncated = true; return; }
            if (text.Length > room) { text = text.Substring(0, room); truncated = true; }
            builder.Append(text);
        }

        string Clip(string text)
        {
            if (text.Length <= MaxBlockChars) return text;
            truncated = true;
            return text.Substring(0, MaxBlockChars) + "…";
        }

        void AppendBlock(string text)
        {
            if (text.Length == 0) return;
            if (builder.Length > 0) Append("\n\n");
            Append(Clip(text));
        }

        void SaveBlob(string kind, string? mimeType, string? data)
        {
            var label = "[" + kind + " " + (string.IsNullOrWhiteSpace(mimeType) ? "application/octet-stream" : mimeType) + "]";
            if (!TryDecode(data, out var bytes))
            {
                unsaved++;
                AppendBlock(label + " the server sent no readable data");
                return;
            }
            if (bytes.Length > MaxBlobBytes)
            {
                unsaved++;
                AppendBlock(label + " " + bytes.Length.ToString("#,0") + " bytes was too large to save");
                return;
            }
            try
            {
                var path = _saveMedia(ExtensionFor(mimeType), bytes);
                attachments++;
                AppendBlock(label + " " + bytes.Length.ToString("#,0") + " bytes · saved to " + path);
            }
            catch (Exception ex)
            {
                unsaved++;
                AppendBlock(label + " " + bytes.Length.ToString("#,0") + " bytes could not be saved: " + ex.Message);
            }
        }

        var blocks = result["content"] as JArray;
        if (blocks != null)
        {
            foreach (var token in blocks)
            {
                if (!(token is JObject block)) continue;
                var type = block["type"]?.Type == JTokenType.String ? block.Value<string>("type") : "unknown";
                switch (type)
                {
                    case "text":
                        AppendBlock(block["text"]?.ToString() ?? "");
                        break;

                    case "image":
                    case "audio":
                        SaveBlob(type, block["mimeType"]?.Value<string>(), block["data"]?.Value<string>());
                        break;

                    case "resource_link":
                        AppendBlock(DescribeResourceLink(block));
                        break;

                    case "resource":
                        DescribeResource(block["resource"] as JObject, AppendBlock, SaveBlob);
                        break;

                    default:
                        AppendBlock("[" + type + " content omitted]");
                        break;
                }
            }
        }

        var structured = result["structuredContent"] as JObject;
        if (structured != null)
        {
            var json = structured.ToString(Formatting.None);
            AppendBlock((builder.Length == 0 ? "" : "Structured content:\n") + json);
        }

        if (builder.Length == 0) Append("(the server returned no content)");
        if (truncated) builder.Append("\n\n[output truncated]");

        var ok = !(result["isError"]?.Type == JTokenType.Boolean && result.Value<bool>("isError"));
        var summary = new List<string> { serverName, tool.Name, ok ? "ok" : "failed" };
        summary.Add(builder.Length + " chars");
        if (attachments > 0) summary.Add(attachments + (attachments == 1 ? " attachment" : " attachments"));
        if (unsaved > 0) summary.Add(unsaved + (unsaved == 1 ? " attachment was not saved" : " attachments were not saved"));
        if (truncated) summary.Add("truncated");

        return new McpFormattedResult
        {
            Content = builder.ToString(),
            Summary = string.Join(" · ", summary),
            Ok = ok,
            Attachments = attachments,
            Unsaved = unsaved,
            Truncated = truncated
        };
    }

    private static void DescribeResource(JObject? resource, Action<string> append, Action<string, string?, string?> save)
    {
        if (resource == null)
        {
            append("[embedded resource without a body]");
            return;
        }
        var uri = resource["uri"]?.Value<string>();
        var mime = resource["mimeType"]?.Value<string>();
        var text = resource["text"];
        if (text != null && text.Type == JTokenType.String)
        {
            append("[resource " + (uri ?? "?") + (string.IsNullOrEmpty(mime) ? "" : " (" + mime + ")") + "]\n" +
                   text.Value<string>());
            return;
        }
        save("resource " + (uri ?? "?"), mime, resource["blob"]?.Value<string>());
    }

    private static string DescribeResourceLink(JObject block)
    {
        var text = new StringBuilder("[resource ");
        text.Append(block["name"]?.Value<string>() ?? block["uri"]?.Value<string>() ?? "?");
        var uri = block["uri"]?.Value<string>();
        if (!string.IsNullOrEmpty(uri)) text.Append(" · ").Append(uri);
        var mime = block["mimeType"]?.Value<string>();
        if (!string.IsNullOrEmpty(mime)) text.Append(" · ").Append(mime);
        var description = block["description"]?.Value<string>();
        if (!string.IsNullOrWhiteSpace(description)) text.Append(" · ").Append(description);
        return text.Append(']').ToString();
    }

    private static bool TryDecode(string? data, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (string.IsNullOrEmpty(data)) return false;
        try
        {
            bytes = Convert.FromBase64String(data);
            return true;
        }
        catch (FormatException) { return false; }
    }

    private static string ExtensionFor(string? mimeType)
    {
        switch ((mimeType ?? "").ToLowerInvariant())
        {
            case "image/png": return ".png";
            case "image/jpeg": return ".jpg";
            case "image/gif": return ".gif";
            case "image/webp": return ".webp";
            case "image/svg+xml": return ".svg";
            case "audio/wav":
            case "audio/x-wav": return ".wav";
            case "audio/mpeg": return ".mp3";
            case "audio/ogg": return ".ogg";
            default: return ".bin";
        }
    }

    private static string SaveUnderLocalAppData(string extension, byte[] bytes)
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LiteLLM.VisualStudio", "mcp", "media");
        Directory.CreateDirectory(folder);
        var name = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" +
                   Guid.NewGuid().ToString("N").Substring(0, 8) + extension;
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
