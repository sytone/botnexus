using BotNexus.Agent.Providers.Core.Utilities;

namespace BotNexus.Agent.Providers.Core.Tests.Utilities;

public class ContextOverflowDetectorTests
{
    [Theory]
    [InlineData("prompt is too long: 213462 tokens > 200000 maximum")]
    [InlineData("Your input exceeds the context window of this model")]
    [InlineData("This model's maximum prompt length is 131072 but the request contains 537812 tokens")]
    [InlineData("400 status code (no body)")]
    [InlineData("Requested token count exceeds the model's maximum context length of 131072 tokens.")]
    [InlineData("Input length (265330) exceeds model's maximum context length (262144).")]
    [InlineData("Input length 131393 exceeds the maximum allowed input length of 131040 tokens.")]
    [InlineData("The input (516368 tokens) is longer than the model's context length (262144 tokens).")]
    [InlineData("Prompt has 5,958,968 tokens, but the configured context size is 256,000 tokens.")]
    public void IsContextOverflow_MatchingMessage_ReturnsTrue(string message)
    {
        ContextOverflowDetector.IsContextOverflow(message).ShouldBeTrue();
    }

    [Theory]
    [InlineData("Throttling error: Too many tokens, please wait before trying again.")]
    [InlineData("rate limit reached: Requested token count exceeds the model's maximum context length of 131072 tokens.")]
    [InlineData("too many requests: Input length (265330) exceeds model's maximum context length (262144).")]
    [InlineData("Service unavailable: Prompt has 5,958,968 tokens, but the configured context size is 256,000 tokens.")]
    [InlineData("rate limit reached")]
    [InlineData("too many requests from this client")]
    [InlineData("internal server error")]
    public void IsContextOverflow_NonOverflowMessage_ReturnsFalse(string message)
    {
        ContextOverflowDetector.IsContextOverflow(message).ShouldBeFalse();
    }

    [Fact]
    public void IsContextOverflow_ExceptionWithInnerOverflow_ReturnsTrue()
    {
        var ex = new Exception("outer", new InvalidOperationException("token limit exceeded"));

        ContextOverflowDetector.IsContextOverflow(ex).ShouldBeTrue();
    }
}
