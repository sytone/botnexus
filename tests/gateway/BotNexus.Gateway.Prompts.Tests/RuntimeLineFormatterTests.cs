using BotNexus.Gateway.Prompts;

namespace BotNexus.Gateway.Prompts.Tests;

public sealed class RuntimeLineFormatterTests
{
    [Fact]
    public void BuildRuntimeLine_WithSessionId_EmitsSessionField()
    {
        var line = RuntimeLineFormatter.BuildRuntimeLine(new PromptRuntimeInfo
        {
            AgentId = "farnsworth",
            SessionId = "979279bc3ead433696aa0ce91bfecb90"
        });

        line.ShouldContain("session=979279bc3ead433696aa0ce91bfecb90");
    }

    [Fact]
    public void BuildRuntimeLine_WithoutSessionId_OmitsSessionField()
    {
        var line = RuntimeLineFormatter.BuildRuntimeLine(new PromptRuntimeInfo
        {
            AgentId = "farnsworth"
        });

        line.ShouldNotContain("session=");
    }

    [Fact]
    public void BuildRuntimeLine_WithWhitespaceSessionId_OmitsSessionField()
    {
        var line = RuntimeLineFormatter.BuildRuntimeLine(new PromptRuntimeInfo
        {
            AgentId = "farnsworth",
            SessionId = "   "
        });

        line.ShouldNotContain("session=");
    }

    [Fact]
    public void BuildRuntimeLine_WithSessionKey_EmitsSessionKeyField()
    {
        var line = RuntimeLineFormatter.BuildRuntimeLine(new PromptRuntimeInfo
        {
            AgentId = "farnsworth",
            SessionKey = "signalr:farnsworth:conv-123"
        });

        line.ShouldContain("session_key=signalr:farnsworth:conv-123");
    }

    [Fact]
    public void BuildRuntimeLine_WithoutSessionKey_OmitsSessionKeyField()
    {
        var line = RuntimeLineFormatter.BuildRuntimeLine(new PromptRuntimeInfo
        {
            AgentId = "farnsworth",
            SessionId = "abc"
        });

        line.ShouldNotContain("session_key=");
    }

    [Fact]
    public void BuildRuntimeLine_EmitsSessionAfterAgent()
    {
        var line = RuntimeLineFormatter.BuildRuntimeLine(new PromptRuntimeInfo
        {
            AgentId = "farnsworth",
            Host = "host-1",
            SessionId = "sess-1"
        });

        // session should appear, and the agent field should precede it for readability.
        var agentIndex = line.IndexOf("agent=", System.StringComparison.Ordinal);
        var sessionIndex = line.IndexOf("session=", System.StringComparison.Ordinal);
        agentIndex.ShouldBeGreaterThanOrEqualTo(0);
        sessionIndex.ShouldBeGreaterThan(agentIndex);
    }

    [Fact]
    public void BuildRuntimeLine_WithBothSessionFields_EmitsBoth()
    {
        var line = RuntimeLineFormatter.BuildRuntimeLine(new PromptRuntimeInfo
        {
            AgentId = "farnsworth",
            SessionId = "sess-1",
            SessionKey = "signalr:farnsworth:conv-9"
        });

        line.ShouldContain("session=sess-1");
        line.ShouldContain("session_key=signalr:farnsworth:conv-9");
    }

    [Fact]
    public void BuildRuntimeLine_NullRuntime_DoesNotThrowAndOmitsSession()
    {
        var line = RuntimeLineFormatter.BuildRuntimeLine(null);

        line.ShouldStartWith("Runtime:");
        line.ShouldNotContain("session=");
    }

    [Fact]
    public void BuildRuntimeLine_PreservesExistingFields_WithSessionAdded()
    {
        var line = RuntimeLineFormatter.BuildRuntimeLine(new PromptRuntimeInfo
        {
            AgentId = "farnsworth",
            Host = "CPC-jobul",
            Provider = "github-copilot",
            Model = "claude-opus-4.8",
            Channel = "signalr",
            SessionId = "sess-42"
        });

        line.ShouldContain("agent=farnsworth");
        line.ShouldContain("host=CPC-jobul");
        line.ShouldContain("provider=github-copilot");
        line.ShouldContain("model=claude-opus-4.8");
        line.ShouldContain("channel=signalr");
        line.ShouldContain("session=sess-42");
    }

    [Fact]
    public void RuntimeContextDelimiters_AreInternalRuntimeContextMarkers()
    {
        RuntimeLineFormatter.RuntimeContextBeginDelimiter.ShouldBe("INTERNAL_RUNTIME_CONTEXT_BEGIN");
        RuntimeLineFormatter.RuntimeContextEndDelimiter.ShouldBe("INTERNAL_RUNTIME_CONTEXT_END");
    }

    [Fact]
    public void RuntimeContextDelimiters_BeginAndEndDiffer()
    {
        RuntimeLineFormatter.RuntimeContextBeginDelimiter
            .ShouldNotBe(RuntimeLineFormatter.RuntimeContextEndDelimiter);
    }

    [Fact]
    public void BuildRuntimeLine_WithMobileClientKind_EmitsClientField()
    {
        var line = RuntimeLineFormatter.BuildRuntimeLine(new PromptRuntimeInfo
        {
            AgentId = "farnsworth",
            Channel = "signalr",
            ClientKind = "mobile"
        });

        line.ShouldContain("client=mobile");
    }

    [Fact]
    public void BuildRuntimeLine_EmitsClientAfterChannel()
    {
        var line = RuntimeLineFormatter.BuildRuntimeLine(new PromptRuntimeInfo
        {
            AgentId = "farnsworth",
            Channel = "signalr",
            ClientKind = "mobile"
        });

        var channelIndex = line.IndexOf("channel=", System.StringComparison.Ordinal);
        var clientIndex = line.IndexOf("client=", System.StringComparison.Ordinal);
        channelIndex.ShouldBeGreaterThanOrEqualTo(0);
        clientIndex.ShouldBeGreaterThan(channelIndex);
    }

    [Fact]
    public void BuildRuntimeLine_WithoutClientKind_OmitsClientField()
    {
        var line = RuntimeLineFormatter.BuildRuntimeLine(new PromptRuntimeInfo
        {
            AgentId = "farnsworth",
            Channel = "signalr"
        });

        line.ShouldNotContain("client=");
    }

    [Fact]
    public void BuildRuntimeLine_WithDesktopClientKind_OmitsClientField()
    {
        // Back-compat: the default desktop kind is the implied baseline and is not
        // surfaced, so existing desktop prompts stay byte-identical (AC#5).
        var line = RuntimeLineFormatter.BuildRuntimeLine(new PromptRuntimeInfo
        {
            AgentId = "farnsworth",
            Channel = "signalr",
            ClientKind = "desktop"
        });

        line.ShouldNotContain("client=");
    }

    [Fact]
    public void BuildRuntimeLine_WithUnknownClientKind_OmitsClientField()
    {
        // Back-compat: an absent hint normalizes to "unknown" upstream and must not
        // emit a client field.
        var line = RuntimeLineFormatter.BuildRuntimeLine(new PromptRuntimeInfo
        {
            AgentId = "farnsworth",
            Channel = "signalr",
            ClientKind = "unknown"
        });

        line.ShouldNotContain("client=");
    }

    [Fact]
    public void BuildRuntimeLine_WithWhitespaceClientKind_OmitsClientField()
    {
        var line = RuntimeLineFormatter.BuildRuntimeLine(new PromptRuntimeInfo
        {
            AgentId = "farnsworth",
            Channel = "signalr",
            ClientKind = "   "
        });

        line.ShouldNotContain("client=");
    }

    [Fact]
    public void BuildRuntimeLine_NormalizesClientKindToLowercase()
    {
        var line = RuntimeLineFormatter.BuildRuntimeLine(new PromptRuntimeInfo
        {
            AgentId = "farnsworth",
            Channel = "signalr",
            ClientKind = "Mobile"
        });

        line.ShouldContain("client=mobile");
    }
}
