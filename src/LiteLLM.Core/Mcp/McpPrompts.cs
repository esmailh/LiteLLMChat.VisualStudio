using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace LiteLLM.Core;

public sealed class McpPromptArgument
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public bool Required { get; set; }
}

/// <summary>One entry of <c>prompts/list</c>. Servers choose prompts, so every field is untrusted display text;
/// the arguments are what the form is built from.</summary>
public sealed class McpPromptInfo
{
    public McpServerConfiguration Server { get; set; } = new McpServerConfiguration();
    public string Name { get; set; } = "";
    public string? Title { get; set; }
    public string? Description { get; set; }
    public IReadOnlyList<McpPromptArgument> Arguments { get; set; } = Array.Empty<McpPromptArgument>();

    public string DisplayName => string.IsNullOrWhiteSpace(Title) ? Name : Title!;

    public static McpPromptInfo From(McpServerConfiguration server, JObject entry)
    {
        var arguments = new List<McpPromptArgument>();
        if (entry["arguments"] is JArray array)
        {
            foreach (var token in array)
            {
                if (!(token is JObject argument)) continue;
                var name = argument["name"]?.Type == JTokenType.String ? argument.Value<string>("name") : null;
                if (string.IsNullOrWhiteSpace(name)) continue;
                arguments.Add(new McpPromptArgument
                {
                    Name = name!,
                    Description = argument["description"]?.Type == JTokenType.String
                        ? argument.Value<string>("description")
                        : null,
                    Required = argument["required"]?.Type == JTokenType.Boolean &&
                               argument.Value<bool>("required")
                });
            }
        }

        return new McpPromptInfo
        {
            Server = server,
            Name = entry["name"]?.Value<string>() ?? "",
            Title = entry["title"]?.Type == JTokenType.String ? entry.Value<string>("title") : null,
            Description = entry["description"]?.Type == JTokenType.String
                ? entry.Value<string>("description")
                : null,
            Arguments = arguments
        };
    }
}

public sealed class McpPromptMessage
{
    public string Role { get; set; } = "user";
    public string Text { get; set; } = "";
}

public sealed class McpPromptResult
{
    public string? Description { get; set; }
    public IReadOnlyList<McpPromptMessage> Messages { get; set; } = Array.Empty<McpPromptMessage>();

    public static McpPromptResult From(JObject result)
    {
        var messages = new List<McpPromptMessage>();
        if (result["messages"] is JArray array)
        {
            foreach (var token in array)
            {
                if (!(token is JObject message)) continue;
                messages.Add(new McpPromptMessage
                {
                    Role = message["role"]?.Type == JTokenType.String ? message.Value<string>("role")! : "user",
                    Text = TextOf(message["content"])
                });
            }
        }
        return new McpPromptResult
        {
            Description = result["description"]?.Type == JTokenType.String
                ? result.Value<string>("description")
                : null,
            Messages = messages
        };
    }

    /// <summary>A prompt message may carry an image or an embedded resource. The composer is text, so anything that
    /// is not text is named rather than silently dropped.</summary>
    private static string TextOf(JToken? content)
    {
        if (!(content is JObject block)) return "";
        var type = block["type"]?.Value<string>() ?? "unknown";
        switch (type)
        {
            case "text":
                return block["text"]?.ToString() ?? "";
            case "resource":
                var text = (block["resource"] as JObject)?["text"];
                return text?.Type == JTokenType.String
                    ? text.Value<string>()!
                    : "[embedded resource]";
            default:
                return "[" + type + " content]";
        }
    }
}

public sealed class McpCompletionResult
{
    public IReadOnlyList<string> Values { get; set; } = Array.Empty<string>();
    public int? Total { get; set; }
    public bool HasMore { get; set; }
    /// <summary>True when the client's own cap cut the list below what the server offered.</summary>
    public bool Truncated { get; set; }

    public static McpCompletionResult From(JObject result)
    {
        var completion = result["completion"] as JObject ?? new JObject();
        var values = new List<string>();
        var truncated = false;
        if (completion["values"] is JArray array)
        {
            foreach (var token in array)
            {
                if (token.Type != JTokenType.String) continue;
                if (values.Count >= McpCompletion.MaxValues) { truncated = true; break; }
                values.Add(token.Value<string>()!);
            }
        }
        return new McpCompletionResult
        {
            Values = values,
            Total = completion["total"]?.Type == JTokenType.Integer ? completion.Value<int?>("total") : null,
            HasMore = completion["hasMore"]?.Type == JTokenType.Boolean && completion.Value<bool>("hasMore"),
            Truncated = truncated
        };
    }
}

/// <summary>Prompt calls, and the completion suggestions both prompt and resource-template forms use.</summary>
public static class McpPrompts
{
    public static Task<McpPage> ListAsync(McpSession session, CancellationToken cancellationToken) =>
        session.ListAllAsync("prompts/list", null, "prompts", cancellationToken: cancellationToken);

    public static async Task<McpPromptResult> GetAsync(McpSession session, string name, JObject? arguments,
        CancellationToken cancellationToken)
    {
        var parameters = new JObject { ["name"] = name };
        if (arguments != null) parameters["arguments"] = arguments;
        var result = await session.RequestAsync("prompts/get", parameters, cancellationToken).ConfigureAwait(false);
        return McpPromptResult.From(result);
    }
}

public static class McpCompletion
{
    /// <summary>A dropdown of suggestions has no business holding a turn; the first hundred are enough and
    /// <see cref="McpCompletionResult.Truncated"/> says the rest exist.</summary>
    public const int MaxValues = 100;

    public static JObject PromptReference(string name) => new JObject
    {
        ["type"] = "ref/prompt",
        ["name"] = name
    };

    public static JObject ResourceReference(string uri) => new JObject
    {
        ["type"] = "ref/resource",
        ["uri"] = uri
    };

    public static async Task<McpCompletionResult> CompleteAsync(McpSession session, JObject reference,
        string argumentName, string argumentValue, JObject? context, CancellationToken cancellationToken)
    {
        var parameters = new JObject
        {
            ["ref"] = reference,
            ["argument"] = new JObject
            {
                ["name"] = argumentName,
                ["value"] = argumentValue
            }
        };
        if (context != null) parameters["context"] = context;

        var result = await session.RequestAsync("completion/complete", parameters, cancellationToken)
            .ConfigureAwait(false);
        return McpCompletionResult.From(result);
    }
}
