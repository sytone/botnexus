using BotNexus.Providers.Core.Utilities;
using FluentAssertions;

namespace BotNexus.Providers.Core.Tests.Utilities;

public class ContextOverflowDetectorTests
{
    [Theory]
    [InlineData("prompt is too long: 213462 tokens > 200000 maximum")]
    [InlineData("Your input exceeds the context window of this model")]
    [InlineData("This model's maximum prompt length is 131072 but the request contains 537812 tokens")]
    [InlineData("400 status code (no body)")]
    public void IsContextOverflow_MatchingMessage_ReturnsTrue(string message)
    {
        ContextOverflowDetector.IsContextOverflow(message).Should().BeTrue();
    }

    [Theory]
    [InlineData("Throttling error: Too many tokens, please wait before trying again.")]
    [InlineData("rate limit reached")]
    [InlineData("too many requests from this client")]
    [InlineData("internal server error")]
    public void IsContextOverflow_NonOverflowMessage_ReturnsFalse(string message)
    {
        ContextOverflowDetector.IsContextOverflow(message).Should().BeFalse();
    }

    [Fact]
    public void IsContextOverflow_ExceptionWithInnerOverflow_ReturnsTrue()
    {
        var ex = new Exception("outer", new InvalidOperationException("token limit exceeded"));

        ContextOverflowDetector.IsContextOverflow(ex).Should().BeTrue();
    }
}
