using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace LiteLLM.Core;

public static class GlabCommand
{
    public static IReadOnlyList<string> ReadArguments(JToken? token)
    {
        if (!(token is JArray array) || array.Count == 0) throw new ArgumentException("glab requires a nonempty arguments array.");
        var result = new List<string>();
        foreach (var value in array)
        {
            if (value.Type != JTokenType.String) throw new ArgumentException("Every glab argument must be a string.");
            var text = value.Value<string>()!;
            if (text.IndexOf('\0') >= 0) throw new ArgumentException("Arguments cannot contain null characters.");
            result.Add(text);
        }
        return result;
    }
}
