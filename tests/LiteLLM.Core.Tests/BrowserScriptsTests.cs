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

    /// <summary>The message for a field that was not found is prose, and prose has apostrophes. It goes in as a JSON
    /// string so one cannot end the literal it sits in — the failure that made every `type` call answer with a
    /// syntax error about the tool instead of a sentence about the field.</summary>
    [Fact]
    public void Fill_PutsTheMissingElementMessageInAsAStringLiteral()
    {
        var script = BrowserScripts.Fill("#user", "admin");

        Assert.Contains(BrowserScripts.Literal("No element matched that selector, so nothing was filled. Pass the CSS selector of the field."),
            script, System.StringComparison.Ordinal);
        Assert.DoesNotContain("__NO_ELEMENT__", script, System.StringComparison.Ordinal);
        Assert.DoesNotContain("\\'", script, System.StringComparison.Ordinal);
    }

    /// <summary>A script that throws has to come back as the page's sentence, not its stack: the frames are the page's
    /// and the model cannot use them, and a stack stops the reading at the first line anyway.</summary>
    [Fact]
    public void Script_ReportsThePagesSentenceWithoutItsStack()
    {
        var script = BrowserScripts.Script("null.value");

        Assert.DoesNotContain("stack", script, System.StringComparison.Ordinal);
        Assert.Contains("e.name+': '+e.message", script, System.StringComparison.Ordinal);
    }

    /// <summary>The wrapper is an expression evaluated in the page, so a helper declared after the outer function has
    /// closed makes the whole call a syntax error — and the page then reports a token, not what the model asked.
    /// Both helpers have to sit inside the function, before the try.</summary>
    [Fact]
    public void Script_IsOneWellFormedExpressionWithItsHelpersInsideIt()
    {
        foreach (var code in new[] { "document.title", "null.value", "const a = 1;\nreturn a" })
        {
            var script = BrowserScripts.Script(code);

            Assert.StartsWith("(async function(){", script, System.StringComparison.Ordinal);
            Assert.EndsWith("})()", script, System.StringComparison.Ordinal);
            Assert.True(script.IndexOf("function pack", System.StringComparison.Ordinal) <
                        script.IndexOf("try{var v=await", System.StringComparison.Ordinal), "pack must be inside the function");
            Assert.True(script.IndexOf("function describe", System.StringComparison.Ordinal) <
                        script.IndexOf("try{var v=await", System.StringComparison.Ordinal), "describe must be inside the function");
        }
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
