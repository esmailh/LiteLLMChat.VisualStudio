using Newtonsoft.Json.Linq;
using Xunit;

namespace LiteLLM.Core.Tests;

/// <summary>How a call's arguments are read when they arrive wrong. Each case is something a model does: it
/// forgets a brace, it quotes the object it was serializing, it sends a list where a path belongs, or it spells
/// a number as a string. All of them used to reach the tool as an empty object, which every tool then read as
/// "the model sent nothing" and answered with a sentence about a path the model had never written.</summary>
public sealed class ToolArgumentsTests
{
    [Fact]
    public void ToolArguments_ParsesAValidObject()
    {
        Assert.True(ToolArguments.TryParse("{\"path\":\"src/Foo.cs\"}", out var args, out var error));
        Assert.Equal("src/Foo.cs", (string)args["path"]!);
        Assert.Equal("", error);
    }

    [Fact]
    public void ToolArguments_ReadsNoArgumentsAsAnEmptyObject()
    {
        // What a tool taking none is sent. Not a failure: there is nothing to have got wrong.
        Assert.True(ToolArguments.TryParse("", out var args, out var error));
        Assert.Empty(args);
        Assert.Equal("", error);
    }

    [Fact]
    public void ToolArguments_ReportsMalformedJsonInsteadOfSwallowingIt()
    {
        Assert.False(ToolArguments.TryParse("{\"path\": \"src/Foo.cs\"", out var args, out var error));
        Assert.Contains("not valid JSON", error);
        Assert.Contains("JSON object", error);          // the fix, not only the fault
        Assert.Empty(args);
    }

    [Fact]
    public void ToolArguments_ReportsArgumentsSentAsAQuotedString()
    {
        // A model that serializes the arguments twice sends a string that is itself valid JSON. Read as an object
        // it would come back empty, and the file it named would be reported as missing.
        Assert.False(ToolArguments.TryParse("\"{\\\"path\\\":\\\"src/Foo.cs\\\"}\"", out _, out var error));
        Assert.Contains("a string", error);
    }

    [Fact]
    public void ToolArguments_ReportsArgumentsSentAsAnArray()
    {
        Assert.False(ToolArguments.TryParse("[1,2]", out _, out var error));
        Assert.Contains("an array", error);
    }

    [Fact]
    public void ToolArguments_ReadsAMissingStringAsNotSupplied()
    {
        Assert.True(ToolArguments.TryText(new JObject(), "path", out var value, out var error));
        Assert.Equal("", value);
        Assert.Equal("", error);
    }

    [Fact]
    public void ToolArguments_ReadsAJsonNullAsNotSupplied()
    {
        var args = new JObject { ["path"] = JValue.CreateNull() };
        Assert.True(ToolArguments.TryText(args, "path", out var value, out _));
        Assert.Equal("", value);
    }

    [Fact]
    public void ToolArguments_RejectsAStringArgumentOfTheWrongType()
    {
        var args = new JObject { ["path"] = new JArray("a", "b") };
        Assert.False(ToolArguments.TryText(args, "path", out var value, out var error));
        Assert.Equal("", value);
        Assert.Contains("`path` must be a string", error);
        Assert.Contains("an array", error);
    }

    [Fact]
    public void ToolArguments_ReadsAnInteger()
    {
        var args = new JObject { ["line_count"] = 200 };
        Assert.True(ToolArguments.TryNumber(args, "line_count", 80, out var value, out var error));
        Assert.Equal(200, value);
        Assert.Equal("", error);
    }

    [Fact]
    public void ToolArguments_KeepsTheFallbackWhenTheNumberIsMissing()
    {
        Assert.True(ToolArguments.TryNumber(new JObject(), "line_count", 80, out var value, out var error));
        Assert.Equal(80, value);
        Assert.Equal("", error);
    }

    [Fact]
    public void ToolArguments_RejectsANumberSentAsAString()
    {
        var args = new JObject { ["line_count"] = "200" };
        Assert.False(ToolArguments.TryNumber(args, "line_count", 200, out var value, out var error));
        Assert.Equal(200, value);   // the caller's own default stands, rather than a guess at what was meant
        Assert.Contains("`line_count` must be an integer", error);
        Assert.Contains("a string", error);
    }

    [Fact]
    public void ToolArguments_RejectsADecimalWhereAnIntegerBelongs()
    {
        var args = new JObject { ["start_line"] = 2.5 };
        Assert.False(ToolArguments.TryNumber(args, "start_line", 1, out var value, out var error));
        Assert.Equal(1, value);
        Assert.Contains("a decimal number", error);
    }
}
