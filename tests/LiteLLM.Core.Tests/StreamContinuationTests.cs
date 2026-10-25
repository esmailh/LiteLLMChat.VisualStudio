using System.Collections.Generic;
using System.Text;
using LiteLLM.Core;
using Xunit;

namespace LiteLLM.Core.Tests;
public sealed class StreamContinuationTests
{
    [Fact]
    public void CanContinue_NeedsAnAnswerAndNoToolCall()
    {
        Assert.True(StreamContinuation.CanContinue(5, false));
        // Nothing came through, so there is nothing to carry into a continuation.
        Assert.False(StreamContinuation.CanContinue(0, false));
        // A tool call arrives in pieces; half of one is not a call that can be run.
        Assert.False(StreamContinuation.CanContinue(5, true));
    }

    [Fact]
    public void Messages_CarryTheAnswerSoFarAndTheInstruction()
    {
        var request = new List<ChatMessage>
        {
            new ChatMessage { Role = "system", Content = "sys" },
            new ChatMessage { Role = "user", Content = "hi" }
        };

        var messages = StreamContinuation.Messages(request, "The answer so far");

        Assert.Equal(4, messages.Count);
        Assert.Equal("The answer so far", messages[2].Content);
        Assert.Equal("assistant", messages[2].Role);
        Assert.Equal(StreamContinuation.Instruction, messages[3].Content);
        Assert.Equal("user", messages[3].Role);
    }

    [Fact]
    public void Filter_DropsTheJunctionTheContinuationRepeats()
    {
        var filter = new StreamContinuationFilter("The quick brown fox jumps over the lazy dog.");

        var text = filter.Accept("the lazy dog. And then it sleeps.") + filter.Flush();

        // The repeated "the lazy dog." is the junction, not new text.
        Assert.Equal(" And then it sleeps.", text);
    }

    [Fact]
    public void Filter_HoldsAcrossDeltasAndReleasesWhatFollowsTheRepeat()
    {
        var filter = new StreamContinuationFilter("... and then the model answered the question completely.");
        var shown = new StringBuilder();

        // A server splits wherever it likes, including in the middle of the repeated sentence.
        shown.Append(filter.Accept(" the question"));
        shown.Append(filter.Accept(" completely. Next"));
        shown.Append(filter.Accept(" paragraph."));
        shown.Append(filter.Flush());

        Assert.Equal(" Next paragraph.", shown.ToString());
    }

    [Fact]
    public void Filter_LeavesAFreshContinuationAlone()
    {
        var filter = new StreamContinuationFilter("The quick brown fox jumps over the lazy dog.");

        var text = filter.Accept("It then sleeps.") + filter.Flush();

        Assert.Equal("It then sleeps.", text);
    }

    [Fact]
    public void Filter_DropsAContinuationThatIsOnlyTheRepeatedJunction()
    {
        var filter = new StreamContinuationFilter("The quick brown fox jumps over the lazy dog.");

        Assert.Equal("", filter.Accept("the lazy dog."));
        Assert.Equal("", filter.Flush());
    }

    [Fact]
    public void Filter_DoesNotTrimAShortCoincidence()
    {
        var filter = new StreamContinuationFilter("The report says the");

        // "the" is the end of the answer and the start of the continuation, but three characters are a
        // coincidence rather than a repeated sentence: cutting them would break real text.
        var text = filter.Accept("the answer is 42.") + filter.Flush();

        Assert.Equal("the answer is 42.", text);
    }

    [Fact]
    public void Filter_PassesEverythingThroughOnceSettled()
    {
        var filter = new StreamContinuationFilter("Done.");
        filter.Accept("And more.");
        var before = filter.Accept("");

        Assert.Equal("", before);
        Assert.Equal(" Still more.", filter.Accept(" Still more."));
    }
}
