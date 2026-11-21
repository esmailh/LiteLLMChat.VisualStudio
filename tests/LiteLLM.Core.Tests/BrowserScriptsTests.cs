using LiteLLM.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace LiteLLM.Core.Tests;

/// <summary>The JavaScript a call sends, checked as text because that is what it is. The rules worth holding are the
/// ones a model's own arguments can break: a target with a quote in it must not end the literal it is pasted into, and
/// a script written as one expression must not be run as a body that returns nothing.</summary>
public sealed class BrowserScriptsTests
{
    [Fact]
    public void Click_PastesTheTargetAsAStringLiteral()
    {
        var target = "Say \"hi\"\nthen leave";

        var script = BrowserScripts.Click(target);

        Assert.Contains(BrowserScripts.Literal(target), script, System.StringComparison.Ordinal);
        // The template is one line, so a newline in the argument would be the only one — and there is none.
        Assert.DoesNotContain("\n", script, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Click_TriesTheSelectorFirstAndTheVisibleTextSecond()
    {
        var script = BrowserScripts.Click("Sign in");

        Assert.Contains("querySelector(q)", script, System.StringComparison.Ordinal);
        Assert.Contains("'text-fragment'", script, System.StringComparison.Ordinal);
        // Which of the two matched is reported, so a selector that hit a link's text is visible rather than silent.
        Assert.Contains("how:how", script, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Fill_PastesBothTheSelectorAndTheValue()
    {
        var script = BrowserScripts.Fill("#user", "a\"b");

        Assert.Contains(BrowserScripts.Literal("#user"), script, System.StringComparison.Ordinal);
        Assert.Contains(BrowserScripts.Literal("a\"b"), script, System.StringComparison.Ordinal);
    }

    /// <summary>Setting a field's value directly leaves a framework believing the field is empty; the native setter is
    /// what makes the page's own listener see the text and keep it on submit.</summary>
    [Fact]
    public void Fill_SetsTheValueTheWayThePageNotices()
    {
        var script = BrowserScripts.Fill("#user", "admin");

        Assert.Contains("getOwnPropertyDescriptor", script, System.StringComparison.Ordinal);
        Assert.Contains("dispatchEvent(new Event('input'", script, System.StringComparison.Ordinal);
        Assert.Contains("isContentEditable", script, System.StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("document.title", false)]
    [InlineData("await fetch('/api/x').then(r => r.text())", false)]
    [InlineData("return 1", true)]
    [InlineData("const rows = document.querySelectorAll('tr');\nreturn rows.length", true)]
    [InlineData("returnValue", false)]
    [InlineData("", false)]
    public void LooksLikeBody_SeparatesAnExpressionFromABlock(string code, bool body) =>
        Assert.Equal(body, BrowserScripts.LooksLikeBody(code));

    [Fact]
    public void Script_KeepsAnExpressionAsTheValue()
    {
        var script = BrowserScripts.Script("document.title");

        Assert.Contains("return (document.title);", script, System.StringComparison.Ordinal);
        Assert.Contains("await", script, System.StringComparison.Ordinal);
        Assert.Contains("function pack", script, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Script_RunsStatementsAsABody()
    {
        var script = BrowserScripts.Script("const a = await fetch('/api').then(r => r.json());\nreturn a.length");

        Assert.Contains("const a = await fetch('/api')", script, System.StringComparison.Ordinal);
        Assert.Contains("return a.length", script, System.StringComparison.Ordinal);
        Assert.DoesNotContain("return (const a", script, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Script_BoundsWhatTheValueCanBe()
    {
        var script = BrowserScripts.Script("document.body.innerHTML");

        Assert.Contains("slice(0," + BrowserScripts.MaxResultChars, script, System.StringComparison.Ordinal);
        Assert.Contains("JSON.stringify(v)", script, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_KeepsTheFirstLineOfAnException()
    {
        var details = new JObject
        {
            ["text"] = "Uncaught",
            ["exception"] = new JObject
            {
                ["description"] = "TypeError: x is null\n    at <anonymous>:1:1"
            }
        };

        Assert.Equal("TypeError: x is null", BrowserScripts.Describe(details));
    }

    [Fact]
    public void Describe_FallsBackToTheProtocolsOwnTextAndNeverThrows()
    {
        Assert.Equal("Uncaught", BrowserScripts.Describe(new JObject { ["text"] = "Uncaught" }));
        Assert.Equal("", BrowserScripts.Describe(null));
        Assert.Equal("", BrowserScripts.Describe(new JObject()));
        Assert.Equal("boom", BrowserScripts.Describe(new JObject { ["exception"] = new JObject { ["value"] = "boom" } }));
    }

    [Fact]
    public void Literal_IsAQuotedJavaScriptString()
    {
        Assert.Equal("\"a\\\"b\"", BrowserScripts.Literal("a\"b"));
        Assert.Equal("\"\"", BrowserScripts.Literal(""));
    }
}
