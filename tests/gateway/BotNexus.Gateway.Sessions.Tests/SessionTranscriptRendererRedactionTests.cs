using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Sessions;

namespace BotNexus.Gateway.Sessions.Tests;

public sealed class SessionTranscriptRendererRedactionTests
{
    [Fact]
    public void RenderMarkdown_RedactsSecretsInContent_WhenEnabled()
    {
        var session = CreateSession(new SessionEntry
        {
            Role = MessageRole.Assistant,
            Content = "my token is ghp_1234567890abcdefABCDEF1234567890abcdEF ok",
            Timestamp = new DateTimeOffset(2026, 6, 12, 10, 31, 0, TimeSpan.Zero)
        });

        var result = SessionTranscriptRenderer.RenderMarkdown(session, "farnsworth", redactSecrets: true);

        Assert.NotNull(result);
        Assert.DoesNotContain("ghp_1234567890abcdefABCDEF1234567890abcdEF", result);
        Assert.Contains("[redacted-secret]", result);
    }

    [Fact]
    public void RenderMarkdown_RedactsSecretsInToolArgs_WhenEnabled()
    {
        var session = CreateSession(new SessionEntry
        {
            Role = MessageRole.Tool,
            Content = "",
            ToolName = "http",
            ToolArgs = "{\"header\":\"Bearer abcDEF123456.tokenBlobHere_9876\"}",
            Timestamp = new DateTimeOffset(2026, 6, 12, 10, 32, 0, TimeSpan.Zero)
        });

        var result = SessionTranscriptRenderer.RenderMarkdown(session, "farnsworth", redactSecrets: true);

        Assert.NotNull(result);
        Assert.DoesNotContain("abcDEF123456.tokenBlobHere_9876", result);
        Assert.Contains("[redacted-secret]", result);
    }

    [Fact]
    public void RenderMarkdown_RedactsSecretsInToolResult_WhenEnabled()
    {
        var session = CreateSession(new SessionEntry
        {
            Role = MessageRole.Tool,
            Content = "output: AKIAIOSFODNN7EXAMPLE more",
            ToolName = "read",
            Timestamp = new DateTimeOffset(2026, 6, 12, 10, 33, 0, TimeSpan.Zero)
        });

        var result = SessionTranscriptRenderer.RenderMarkdown(session, "farnsworth", redactSecrets: true);

        Assert.NotNull(result);
        Assert.DoesNotContain("AKIAIOSFODNN7EXAMPLE", result);
        Assert.Contains("[redacted-secret]", result);
    }

    [Fact]
    public void RenderMarkdown_IsByteIdentical_WhenDisabledByDefault()
    {
        var entry = new SessionEntry
        {
            Role = MessageRole.Assistant,
            Content = "my token is ghp_1234567890abcdefABCDEF1234567890abcdEF ok",
            Timestamp = new DateTimeOffset(2026, 6, 12, 10, 31, 0, TimeSpan.Zero)
        };

        var defaultResult = SessionTranscriptRenderer.RenderMarkdown(CreateSession(entry), "farnsworth");
        var explicitOff = SessionTranscriptRenderer.RenderMarkdown(CreateSession(entry), "farnsworth", redactSecrets: false);

        Assert.NotNull(defaultResult);
        Assert.Equal(defaultResult, explicitOff);
        Assert.Contains("ghp_1234567890abcdefABCDEF1234567890abcdEF", defaultResult);
        Assert.DoesNotContain("[redacted-secret]", defaultResult);
    }

    private static GatewaySession CreateSession(params SessionEntry[] entries)
    {
        var session = new Session
        {
            SessionId = SessionId.From("test-session-1"),
            ConversationId = ConversationId.From("c_test"),
            CreatedAt = new DateTimeOffset(2026, 6, 12, 10, 0, 0, TimeSpan.Zero),
            Status = SessionStatus.Active,
        };
        session.History.AddRange(entries);
        return new GatewaySession(session);
    }
}
