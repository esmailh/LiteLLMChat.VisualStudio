using System;
using LiteLLM.Core;
using Xunit;

namespace LiteLLM.Core.Tests;

/// <summary>The one rejection compaction can answer: a provider saying the whole request is too long. It has to be
/// recognised from the body a proxy happened to send, and a 400 for anything else has to be left alone — folding a
/// conversation because a tool name was malformed would spend a call and change nothing.</summary>
public sealed class ContextOverflowTests
{
    private static LiteLlmException Failure(int status, string body) =>
        new LiteLlmException("the request failed", "", new Exception()) { Exchange = new HttpExchange { Status = status, ResponseBody = body } };

    [Theory]
    [InlineData(400, "{\"error\":{\"code\":\"context_length_exceeded\",\"message\":\"too long\"}}")]
    [InlineData(400, "This model's maximum context length is 128000 tokens.")]
    [InlineData(400, "The prompt is too long for this model.")]
    [InlineData(400, "input is too long")]
    [InlineData(413, "string too long")]
    [InlineData(400, "maximum number of tokens exceeded")]
    [InlineData(400, "Please reduce the length of the messages.")]
    [InlineData(400, "This model supports a maximum context window of 8192")]
    [InlineData(400, "too many tokens in the request")]
    [InlineData(400, "the request exceeds the model's maximum")]
    public void ContextOverflow_RecognisesTheProvidersOwnWording(int status, string body) =>
        Assert.True(ContextOverflow.IsOverflow(Failure(status, body)));

    [Theory]
    [InlineData(400, "{\"error\":{\"message\":\"Invalid value for 'temperature'\"}}")]
    [InlineData(400, "unexpected tool_use id")]
    [InlineData(401, "context_length_exceeded")]     // an authentication failure is never a size one
    [InlineData(500, "context_length_exceeded")]     // nor is a server fault
    [InlineData(429, "rate limit reached")]
    public void ContextOverflow_LeavesEveryOtherFailureAlone(int status, string body) =>
        Assert.False(ContextOverflow.IsOverflow(Failure(status, body)));

    [Fact]
    public void ContextOverflow_AcceptsAFailureWhoseStatusWasNeverRecorded()
    {
        // A client that failed before the response was captured still carries the provider's sentence, and wrongly
        // folding costs one call while refusing costs the turn.
        var failure = new LiteLlmException("maximum context length exceeded", "", null);
        Assert.True(ContextOverflow.IsOverflow(failure));
        Assert.False(ContextOverflow.IsOverflow(new LiteLlmException("connection refused", "", null)));
        Assert.False(ContextOverflow.IsOverflow(null!));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("context window exceeded", true)]
    [InlineData("CONTEXT LENGTH", true)]
    [InlineData("the file was too long to read", false)]
    public void ContextOverflow_AnswersTheSameQuestionAboutPlainText(string? text, bool matches) =>
        Assert.Equal(matches, ContextOverflow.LooksLike(text));
}
