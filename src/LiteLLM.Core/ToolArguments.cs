using System;
using Newtonsoft.Json.Linq;

namespace LiteLLM.Core;

/// <summary>Reads the arguments a model sent for a tool call, and says so when it cannot.
///
/// The report is the point. These arguments come from a model rather than from a person, so a call with a
/// missing brace, with the whole object quoted as a string, or with a number where a path belongs is an
/// ordinary event. Emptying the object instead — which is what a swallowing parse does — turns all of them
/// into the same silent "No such file", and the model then hunts for a bug in a path it never sent.
///
/// Absent is not an error: an optional argument is simply not there. A present argument of the wrong type is,
/// because the only way to carry on is to guess, and a guess the model is not told about is worse than a
/// refusal it can read.</summary>
public static class ToolArguments
{
    /// <summary>Parses whole call arguments. No arguments at all is a legitimate empty object, which is what a
    /// tool taking none is sent.</summary>
    public static bool TryParse(string? json, out JObject args, out string error)
    {
        args = new JObject();
        error = "";
        // Spelled out rather than with IsNullOrWhiteSpace, whose net48 reference assembly carries no annotation and
        // so would leave the parse below looking like it could be handed a null.
        if (json == null || json.Trim().Length == 0) return true;
        try
        {
            var token = JToken.Parse(json);
            // Narrowed rather than cast: a model that serializes the arguments twice sends a string that is
            // itself valid JSON, and that must not be mistaken for the object it describes.
            if (token is JObject parsed) { args = parsed; return true; }
            error = "The call arguments arrived as " + Kind(token) + " instead of a JSON object. Send them as an object, such as {\"path\": \"src/Foo.cs\"}.";
            return false;
        }
        catch (Exception ex)
        {
            error = "The call arguments were not valid JSON, so no argument could be read from them. Send them as a JSON object, such as {\"path\": \"src/Foo.cs\"}. The parser said: " + ex.Message;
            return false;
        }
    }

    /// <summary>A string argument. Missing, or present as JSON null, is "not supplied", and the caller decides
    /// whether its own default is good enough.</summary>
    public static bool TryText(JObject? args, string name, out string value, out string error)
    {
        value = "";
        error = "";
        var token = args?[name];
        if (token == null || token.Type == JTokenType.Null) return true;
        if (token.Type == JTokenType.String) { value = (string?)token ?? ""; return true; }
        error = "`" + name + "` must be a string; it arrived as " + Kind(token) + ".";
        return false;
    }

    /// <summary>A whole-number argument. A number sent as a string is refused rather than converted: the schema
    /// says integer, and quietly accepting `"200"` teaches the model that its own sloppiness works.</summary>
    public static bool TryNumber(JObject? args, string name, int fallback, out int value, out string error)
    {
        value = fallback;
        error = "";
        var token = args?[name];
        if (token == null || token.Type == JTokenType.Null) return true;
        if (token.Type == JTokenType.Integer)
        {
            value = token.Value<int>();
            return true;
        }
        error = "`" + name + "` must be an integer; it arrived as " + Kind(token) + ".";
        return false;
    }

    /// <summary>What arrived, in the words the message needs. The model reads this to find its own mistake, so
    /// "an array" tells it more than "invalid".</summary>
    private static string Kind(JToken token) => token.Type switch
    {
        JTokenType.Array => "an array",
        JTokenType.Object => "a JSON object",
        JTokenType.String => "a string",
        JTokenType.Integer => "a whole number",
        JTokenType.Float => "a decimal number",
        JTokenType.Boolean => "a boolean",
        JTokenType.Null => "null",
        _ => "a " + token.Type.ToString().ToLowerInvariant()
    };
}
