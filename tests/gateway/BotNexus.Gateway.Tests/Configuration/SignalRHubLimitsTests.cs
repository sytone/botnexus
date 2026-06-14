using System.Text.Json;
using BotNexus.Gateway.Api;
using BotNexus.Gateway.Configuration;
using Microsoft.AspNetCore.SignalR;

namespace BotNexus.Gateway.Tests.Configuration;

/// <summary>
/// Verifies the explicit SignalR hub limits applied by <see cref="SignalRHubLimits"/> and the
/// binding of the optional <see cref="SignalRConfig"/> section. Closes a security gap where
/// <c>AddSignalR()</c> ran with implicit framework defaults (no explicit
/// <see cref="HubOptions.MaximumReceiveMessageSize"/>, <c>MaximumParallelInvocationsPerClient</c>,
/// or <see cref="HubOptions.StreamBufferCapacity"/>) so oversized frames and unbounded
/// concurrency could not be intentionally capped or documented.
/// </summary>
public sealed class SignalRHubLimitsTests
{
    [Fact]
    public void Apply_WithNullConfig_UsesSecureDefaults()
    {
        var options = new HubOptions();

        SignalRHubLimits.Apply(options, config: null);

        // Defaults are explicit and intentional, not the framework's implicit 32 KB / 1 / 10.
        options.MaximumReceiveMessageSize.ShouldBe(SignalRHubLimits.DefaultMaximumReceiveMessageSizeBytes);
        options.MaximumParallelInvocationsPerClient.ShouldBe(SignalRHubLimits.DefaultMaximumParallelInvocationsPerClient);
        options.StreamBufferCapacity.ShouldBe(SignalRHubLimits.DefaultStreamBufferCapacity);
    }

    [Fact]
    public void Apply_WithEmptyConfig_UsesSecureDefaults()
    {
        var options = new HubOptions();

        SignalRHubLimits.Apply(options, new SignalRConfig());

        options.MaximumReceiveMessageSize.ShouldBe(SignalRHubLimits.DefaultMaximumReceiveMessageSizeBytes);
        options.MaximumParallelInvocationsPerClient.ShouldBe(SignalRHubLimits.DefaultMaximumParallelInvocationsPerClient);
        options.StreamBufferCapacity.ShouldBe(SignalRHubLimits.DefaultStreamBufferCapacity);
    }

    [Fact]
    public void Apply_WithExplicitConfig_OverridesDefaults()
    {
        var options = new HubOptions();
        var config = new SignalRConfig
        {
            MaximumReceiveMessageSizeBytes = 2_000_000,
            MaximumParallelInvocationsPerClient = 4,
            StreamBufferCapacity = 20,
        };

        SignalRHubLimits.Apply(options, config);

        options.MaximumReceiveMessageSize.ShouldBe(2_000_000L);
        options.MaximumParallelInvocationsPerClient.ShouldBe(4);
        options.StreamBufferCapacity.ShouldBe(20);
    }

    [Fact]
    public void Apply_DefaultReceiveSize_AccommodatesInlineMedia()
    {
        // SendMessageWithMedia carries base64-encoded media inline through the hub. Base64
        // inflates payloads by ~33%, so the default cap must comfortably exceed the framework's
        // 32 KB default to avoid silently rejecting legitimate image/audio sends, while still
        // bounding runaway frames.
        SignalRHubLimits.DefaultMaximumReceiveMessageSizeBytes.ShouldBeGreaterThan(1_000_000L);
        // ...but not unbounded — keep a hard ceiling so a single frame can't exhaust memory.
        SignalRHubLimits.DefaultMaximumReceiveMessageSizeBytes.ShouldBeLessThanOrEqualTo(50_000_000L);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-1_000_000)]
    public void Apply_WithNonPositiveReceiveSize_FallsBackToDefault(long invalid)
    {
        var options = new HubOptions();

        SignalRHubLimits.Apply(options, new SignalRConfig { MaximumReceiveMessageSizeBytes = invalid });

        options.MaximumReceiveMessageSize.ShouldBe(SignalRHubLimits.DefaultMaximumReceiveMessageSizeBytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Apply_WithNonPositiveParallelInvocations_FallsBackToDefault(int invalid)
    {
        var options = new HubOptions();

        SignalRHubLimits.Apply(options, new SignalRConfig { MaximumParallelInvocationsPerClient = invalid });

        options.MaximumParallelInvocationsPerClient.ShouldBe(SignalRHubLimits.DefaultMaximumParallelInvocationsPerClient);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Apply_WithNonPositiveStreamBufferCapacity_FallsBackToDefault(int invalid)
    {
        var options = new HubOptions();

        SignalRHubLimits.Apply(options, new SignalRConfig { StreamBufferCapacity = invalid });

        options.StreamBufferCapacity.ShouldBe(SignalRHubLimits.DefaultStreamBufferCapacity);
    }

    [Fact]
    public void SignalRConfig_BindsFromGatewaySettingsJson()
    {
        const string json = """
        {
          "gateway": {
            "signalR": {
              "maximumReceiveMessageSizeBytes": 5242880,
              "maximumParallelInvocationsPerClient": 8,
              "streamBufferCapacity": 15
            }
          }
        }
        """;

        var platform = JsonSerializer.Deserialize<PlatformConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        });

        platform.ShouldNotBeNull();
        platform!.Gateway.ShouldNotBeNull();
        platform.Gateway!.SignalR.ShouldNotBeNull();
        platform.Gateway.SignalR!.MaximumReceiveMessageSizeBytes.ShouldBe(5_242_880L);
        platform.Gateway.SignalR.MaximumParallelInvocationsPerClient.ShouldBe(8);
        platform.Gateway.SignalR.StreamBufferCapacity.ShouldBe(15);
    }

    [Fact]
    public void SignalRConfig_AbsentSection_BindsToNull()
    {
        const string json = """
        {
          "gateway": {
            "cors": { "allowedOrigins": ["http://localhost:5005"] }
          }
        }
        """;

        var platform = JsonSerializer.Deserialize<PlatformConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        });

        platform.ShouldNotBeNull();
        platform!.Gateway.ShouldNotBeNull();
        platform.Gateway!.SignalR.ShouldBeNull();
    }
}
