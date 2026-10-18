using LiteLLM.Core;
using Xunit;

namespace LiteLLM.Core.Tests;
public sealed class SymbolReferenceParserTests
{
    [Fact]
    public void Find_ReportsADottedChainInProse()
    {
        var matches = SymbolReferenceParser.Find("Then ChatStateLimits.Sanitize repairs the transcript.", false);

        var match = Assert.Single(matches);
        Assert.Equal("ChatStateLimits.Sanitize", match.Name);
        Assert.Equal(5, match.Index);
        Assert.Equal("ChatStateLimits.Sanitize".Length, match.Length);
    }

    [Fact]
    public void Find_LeavesABareNameInProseAlone()
    {
        // Linking every capitalised word would make a paragraph about UsageLedger a field of links, and a solution
        // that declares Program would link the English noun.
        var matches = SymbolReferenceParser.Find("UsageLedger keeps the totals.", false);

        Assert.Empty(matches);
    }

    [Fact]
    public void Find_ReportsAWholeCodeSpanThatIsOneName()
    {
        var matches = SymbolReferenceParser.Find("UsageLedger", true);

        var match = Assert.Single(matches);
        Assert.Equal("UsageLedger", match.Name);
        Assert.Equal(0, match.Index);
        Assert.Equal(11, match.Length);
    }

    [Fact]
    public void Find_ReportsTheIdentifierBeforeCallParentheses()
    {
        var matches = SymbolReferenceParser.Find("Sanitize()", true);

        var match = Assert.Single(matches);
        Assert.Equal("Sanitize", match.Name);
        Assert.Equal(0, match.Index);
        Assert.Equal("Sanitize".Length, match.Length);
    }

    [Fact]
    public void Find_ReportsTheIdentifierBeforeTypeArguments()
    {
        var matches = SymbolReferenceParser.Find("UsageLedger<T>", true);

        var match = Assert.Single(matches);
        Assert.Equal("UsageLedger", match.Name);
        Assert.Equal("UsageLedger".Length, match.Length);
    }

    [Fact]
    public void Find_DoesNotTreatALongerCodeSpanAsOneName()
    {
        var matches = SymbolReferenceParser.Find("new UsageLedger()", true);

        Assert.Empty(matches);
    }

    [Fact]
    public void Find_StillReportsADottedChainInsideALongerCodeSpan()
    {
        var matches = SymbolReferenceParser.Find("UsageLedger.For(model)", true);

        var match = Assert.Single(matches);
        Assert.Equal("UsageLedger.For", match.Name);
    }

    [Fact]
    public void Find_IgnoresAChainInsideAUrlAnEmailOrAPath()
    {
        Assert.Empty(SymbolReferenceParser.Find("https://example.com/Path.Name", false));
        Assert.Empty(SymbolReferenceParser.Find("User.Name@example.com", false));
        Assert.Empty(SymbolReferenceParser.Find("src\\LiteLLM.Core\\UsageLedger.cs", false));
    }

    [Fact]
    public void Find_IgnoresALowercaseChain()
    {
        // `file.name` is not the shape of a C# declaration reference.
        Assert.Empty(SymbolReferenceParser.Find("the file.name field", false));
    }
}
