using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace LiteLLM.Core;

/// <summary>One entry of <c>resources/list</c>. The URI is the identity; everything else is display metadata the
/// server controls, so it is treated as text.</summary>
public sealed class McpResourceInfo
{
    public McpServerConfiguration Server { get; set; } = new McpServerConfiguration();
    public string Uri { get; set; } = "";
    public string? Name { get; set; }
    public string? MimeType { get; set; }
    public string? Description { get; set; }
    public long? Size { get; set; }

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Uri : Name!;

    public static McpResourceInfo From(McpServerConfiguration server, JObject entry) => new McpResourceInfo
    {
        Server = server,
        Uri = entry["uri"]?.Value<string>() ?? "",
        Name = entry["name"]?.Type == JTokenType.String ? entry.Value<string>("name") : null,
        MimeType = entry["mimeType"]?.Type == JTokenType.String ? entry.Value<string>("mimeType") : null,
        Description = entry["description"]?.Type == JTokenType.String ? entry.Value<string>("description") : null,
        Size = entry["size"]?.Type == JTokenType.Integer ? entry.Value<long>("size") : null
    };
}

/// <summary>One entry of <c>resources/templates/list</c>: a URI template such as <c>file:///{path}</c>.</summary>
public sealed class McpResourceTemplate
{
    public McpServerConfiguration Server { get; set; } = new McpServerConfiguration();
    public string UriTemplate { get; set; } = "";
    public string? Name { get; set; }
    public string? MimeType { get; set; }
    public string? Description { get; set; }

    public static McpResourceTemplate From(McpServerConfiguration server, JObject entry) => new McpResourceTemplate
    {
        Server = server,
        UriTemplate = entry["uriTemplate"]?.Value<string>() ?? "",
        Name = entry["name"]?.Type == JTokenType.String ? entry.Value<string>("name") : null,
        MimeType = entry["mimeType"]?.Type == JTokenType.String ? entry.Value<string>("mimeType") : null,
        Description = entry["description"]?.Type == JTokenType.String ? entry.Value<string>("description") : null
    };
}

/// <summary>One body inside a <c>resources/read</c> answer. Text is kept as text; a blob is decoded once, here, so
/// the caller can save it without dealing with base64. <see cref="Problem"/> is set when the body could not be
/// read, which is different from an empty body.</summary>
public sealed class McpResourceContents
{
    public string Uri { get; set; } = "";
    public string? MimeType { get; set; }
    public string? Text { get; set; }
    public byte[]? Blob { get; set; }
    public string? Problem { get; set; }

    public static McpResourceContents From(JObject entry)
    {
        var contents = new McpResourceContents
        {
            Uri = entry["uri"]?.Value<string>() ?? "",
            MimeType = entry["mimeType"]?.Type == JTokenType.String ? entry.Value<string>("mimeType") : null,
            Text = entry["text"]?.Type == JTokenType.String ? entry.Value<string>("text") : null
        };
        var blob = entry["blob"]?.Type == JTokenType.String ? entry.Value<string>("blob") : null;
        if (blob == null) return contents;
        try { contents.Blob = Convert.FromBase64String(blob); }
        catch (FormatException) { contents.Problem = "the blob was not valid base64"; }
        return contents;
    }

    /// <summary>The text the transcript shows. A blob is saved through <paramref name="saveBlob"/> and named;
    /// when no sink is supplied it is named without being written.</summary>
    public string ToText(Func<string, byte[], string>? saveBlob)
    {
        if (Text != null) return Clip(Text);
        if (Problem != null) return "[" + Uri + ": " + Problem + "]";
        if (Blob == null) return "[" + Uri + ": no content]";

        var label = "[" + Uri + (string.IsNullOrEmpty(MimeType) ? "" : " · " + MimeType) + " · " +
                    Blob.Length.ToString("#,0") + " bytes";
        if (saveBlob == null) return label + "]";
        try { return label + " · saved to " + saveBlob(".bin", Blob) + "]"; }
        catch (Exception ex) { return label + " could not be saved: " + ex.Message + "]"; }
    }

    private static string Clip(string text) =>
        text.Length <= McpResultFormatter.MaxBlockChars
            ? text
            : text.Substring(0, McpResultFormatter.MaxBlockChars) + "…";
}

public sealed class McpResourceRead
{
    public IReadOnlyList<McpResourceContents> Contents { get; set; } = Array.Empty<McpResourceContents>();

    public static McpResourceRead From(JObject result)
    {
        var contents = new List<McpResourceContents>();
        if (result["contents"] is JArray array)
            foreach (var entry in array)
                if (entry is JObject body) contents.Add(McpResourceContents.From(body));
        return new McpResourceRead { Contents = contents };
    }
}

/// <summary>Resource calls on an open session. Listing follows pagination; reading returns every body the server
/// sent, including ones it could not decode, so a partially readable answer is not reported as empty.</summary>
public static class McpResources
{
    public static Task<McpPage> ListAsync(McpSession session, CancellationToken cancellationToken) =>
        session.ListAllAsync("resources/list", null, "resources", cancellationToken: cancellationToken);

    public static Task<McpPage> ListTemplatesAsync(McpSession session, CancellationToken cancellationToken) =>
        session.ListAllAsync("resources/templates/list", null, "resourceTemplates",
            cancellationToken: cancellationToken);

    public static async Task<McpResourceRead> ReadAsync(McpSession session, string uri,
        CancellationToken cancellationToken)
    {
        var result = await session.RequestAsync("resources/read", new JObject { ["uri"] = uri }, cancellationToken)
            .ConfigureAwait(false);
        return McpResourceRead.From(result);
    }

    /// <summary>Subscribes to one resource. The stateless revision groups subscriptions into a listen stream, so
    /// the handle must be disposed to unsubscribe; the session-oriented revisions have their own requests and
    /// return null.</summary>
    public static async Task<McpSubscription?> SubscribeAsync(McpSession session, string uri,
        CancellationToken cancellationToken)
    {
        if (session.Era == McpEra.Modern)
            return await session.ListenAsync(new JObject
            {
                ["resourceSubscriptions"] = new JArray(uri)
            }, cancellationToken).ConfigureAwait(false);

        await session.RequestAsync("resources/subscribe", new JObject { ["uri"] = uri }, cancellationToken)
            .ConfigureAwait(false);
        return null;
    }

    public static async Task UnsubscribeAsync(McpSession session, string uri, McpSubscription? subscription,
        CancellationToken cancellationToken)
    {
        if (session.Era == McpEra.Modern)
        {
            subscription?.Dispose();
            return;
        }
        await session.RequestAsync("resources/unsubscribe", new JObject { ["uri"] = uri }, cancellationToken)
            .ConfigureAwait(false);
    }
}
