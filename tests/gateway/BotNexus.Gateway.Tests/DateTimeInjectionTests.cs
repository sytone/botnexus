using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Routing;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Unit tests for the datetime injection feature in GatewayHost.InjectDateTimeIfEnabled.
/// </summary>
public sealed class DateTimeInjectionTests
{
    // ── Helper: build a minimal GatewayHost with optional PlatformConfig ──

    private static GatewayHost CreateHostWithConfig(PlatformConfig? config)
    {
        var platformOptions = config is null ? null : Options.Create(config);
        return new GatewayHost(
            Mock.Of<IAgentSupervisor>(),
            Mock.Of<IMessageRouter>(),
            Mock.Of<ISessionStore>(),
            Mock.Of<IActivityBroadcaster>(),
            Mock.Of<IChannelManager>(),
            Mock.Of<ISessionCompactor>(),
            new TestOptionsMonitor<CompactionOptions>(new CompactionOptions()),
            NullLogger<GatewayHost>.Instance,
            platformConfig: platformOptions);
    }

    // ── Test 1: injection disabled by default ──

    [Fact]
    public void InjectDateTimeIfEnabled_WhenNotConfigured_ReturnsContentUnchanged()
    {
        var host = CreateHostWithConfig(null);
        var result = host.InjectDateTimeIfEnabled("Hello world", descriptor: null);
        result.ShouldBe("Hello world");
    }

    // ── Test 2: world-level injection enabled ──

    [Fact]
    public void InjectDateTimeIfEnabled_WhenWorldEnabled_PrependsDatetimeTag()
    {
        var config = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                DateTimeInjection = new DateTimeInjectionConfig { Enabled = true, Timezone = "UTC" }
            }
        };
        var host = CreateHostWithConfig(config);
        var result = host.InjectDateTimeIfEnabled("Hello", descriptor: null);
        result.ShouldStartWith("<currentdatetime>");
        result.ShouldContain("</currentdatetime>\nHello");
        result.ShouldContain("UTC");
    }

    // ── Test 3: per-agent disabled overrides world enabled ──

    [Fact]
    public void InjectDateTimeIfEnabled_WhenAgentDisabled_ReturnsContentUnchanged()
    {
        var config = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                DateTimeInjection = new DateTimeInjectionConfig { Enabled = true, Timezone = "UTC" }
            }
        };
        var host = CreateHostWithConfig(config);
        var descriptor = new AgentDescriptor
        {
            AgentId = Domain.Primitives.AgentId.From("agent-1"),
            DisplayName = "Agent 1",
            ModelId = "gpt-4o",
            ApiProvider = "openai",
            DateTimeInjection = new DateTimeInjectionConfig { Enabled = false }
        };
        var result = host.InjectDateTimeIfEnabled("Hello", descriptor);
        result.ShouldBe("Hello");
    }

    // ── Test 4: per-agent timezone overrides world ──

    [Fact]
    public void InjectDateTimeIfEnabled_WhenAgentTimezoneSet_UsesAgentTimezone()
    {
        var config = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                DateTimeInjection = new DateTimeInjectionConfig { Enabled = true, Timezone = "UTC" }
            }
        };
        var host = CreateHostWithConfig(config);
        var descriptor = new AgentDescriptor
        {
            AgentId = Domain.Primitives.AgentId.From("agent-2"),
            DisplayName = "Agent 2",
            ModelId = "gpt-4o",
            ApiProvider = "openai",
            DateTimeInjection = new DateTimeInjectionConfig { Enabled = true, Timezone = "America/New_York" }
        };
        var result = host.InjectDateTimeIfEnabled("Hello", descriptor);
        result.ShouldContain("America/New_York");
    }

    // ── Test 5: invalid timezone falls back to UTC ──

    [Fact]
    public void InjectDateTimeIfEnabled_WhenInvalidTimezone_FallsBackToUtc()
    {
        var config = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                DateTimeInjection = new DateTimeInjectionConfig { Enabled = true, Timezone = "Not/A/Valid/Zone" }
            }
        };
        var host = CreateHostWithConfig(config);
        var result = host.InjectDateTimeIfEnabled("Hello", descriptor: null);
        result.ShouldStartWith("<currentdatetime>");
        // Should still produce a valid ISO8601-like tag (fallen back to UTC)
        result.ShouldContain("</currentdatetime>\nHello");
    }

    // ── Test 6: world default timezone fallback ──

    [Fact]
    public void InjectDateTimeIfEnabled_WhenNoTimezoneSet_FallsBackToDefaultTimezone()
    {
        var config = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                DefaultTimezone = "Europe/London",
                DateTimeInjection = new DateTimeInjectionConfig { Enabled = true }
                // Timezone not set on injection config — should fall back to DefaultTimezone
            }
        };
        var host = CreateHostWithConfig(config);
        var result = host.InjectDateTimeIfEnabled("Hello", descriptor: null);
        result.ShouldContain("Europe/London");
    }

    // ── Test 7: injection format — well-formed XML tag ──

    [Fact]
    public void InjectDateTimeIfEnabled_WhenEnabled_ProducesWellFormedXmlTag()
    {
        var config = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                DateTimeInjection = new DateTimeInjectionConfig { Enabled = true, Timezone = "UTC" }
            }
        };
        var host = CreateHostWithConfig(config);
        var result = host.InjectDateTimeIfEnabled("My message", descriptor: null);
        // Format: <currentdatetime>2026-06-05T14:32:00+00:00 (UTC)</currentdatetime>\nMy message
        result.ShouldStartWith("<currentdatetime>");
        result.ShouldContain("</currentdatetime>\nMy message");
        // The datetime tag should contain the timezone in parentheses
        result.ShouldContain("(UTC)");
    }
}
