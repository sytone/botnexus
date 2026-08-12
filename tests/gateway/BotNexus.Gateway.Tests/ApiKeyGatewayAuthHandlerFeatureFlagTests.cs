using System.Text.Json;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Security;
using Microsoft.Extensions.Logging;
using Microsoft.FeatureManagement;
using Shouldly;
using Xunit;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Pins #2767 AC6: an absent feature flag is observable in the log, while every fail-open fallback
/// in the dev-origin guard stays exactly as it was.
/// <para>
/// The fail-open assertions are the important half. The guard rejects requests by Origin, so making
/// it enforce on a fallback path would lock a keyless operator out of their own gateway on restart.
/// #2767 changes visibility only; these tests exist so a later "tightening" cannot quietly turn a
/// logging change into a lockout.
/// </para>
/// </summary>
public sealed class ApiKeyGatewayAuthHandlerFeatureFlagTests
{
    // ── AC6: absence is logged, once ────────────────────────────────────────────────────

    [Fact]
    public async Task AbsentFlag_LogsWarningNamingTheFlagAndTheDefault()
    {
        var logger = new CapturingLogger();
        var handler = new ApiKeyGatewayAuthHandler(
            new PlatformConfig(), // no FeatureManagement section at all
            logger,
            securityEvents: null,
            featureManager: new StubFeatureManager(enabled: false));

        await handler.AuthenticateAsync(CreateContext());

        var warning = logger.Warnings.ShouldHaveSingleItem();
        warning.ShouldContain(FeatureFlags.GatewayDevOriginEnforcement);
        warning.ShouldContain("absent");
        warning.ShouldContain("False", Case.Insensitive);
    }

    [Fact]
    public async Task AbsentFlag_LogsOnlyOnceAcrossManyHandshakes()
    {
        // This sits on the authentication hot path; an unthrottled warning would drown the log.
        var logger = new CapturingLogger();
        var handler = new ApiKeyGatewayAuthHandler(
            new PlatformConfig(),
            logger,
            securityEvents: null,
            featureManager: new StubFeatureManager(enabled: false));

        for (var i = 0; i < 5; i++)
            await handler.AuthenticateAsync(CreateContext());

        logger.Warnings.Count.ShouldBe(1);
    }

    [Fact]
    public async Task PresentFlag_LogsNoAbsenceWarning()
    {
        // Sad path for the warning itself: a stated decision must not be nagged about, or the
        // signal is worthless.
        var logger = new CapturingLogger();
        var config = new PlatformConfig
        {
            FeatureManagement = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
            {
                [FeatureFlags.GatewayDevOriginEnforcement] = JsonDocument.Parse("false").RootElement
            }
        };

        var handler = new ApiKeyGatewayAuthHandler(
            config,
            logger,
            securityEvents: null,
            featureManager: new StubFeatureManager(enabled: false));

        await handler.AuthenticateAsync(CreateContext());

        logger.Warnings.ShouldBeEmpty();
    }

    // ── AC6: fail-open behaviour is UNCHANGED ───────────────────────────────────────────

    [Fact]
    public async Task AbsentFlag_StillFailsOpen_DisallowedOriginSucceeds()
    {
        // The whole point of #2767 is that the fallback becomes visible, NOT that it changes.
        var config = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                Cors = new CorsConfig { AllowedOrigins = ["http://localhost:5005"] }
            }
        };

        var handler = new ApiKeyGatewayAuthHandler(
            config,
            new CapturingLogger(),
            securityEvents: null,
            featureManager: new StubFeatureManager(enabled: false));

        var result = await handler.AuthenticateAsync(
            CreateContext(new Dictionary<string, string> { ["Origin"] = "http://evil.example.com" }));

        result.IsAuthenticated.ShouldBeTrue();
        result.Identity!.CallerId.ShouldBe("gateway-dev");
    }

    [Fact]
    public async Task EvaluationThrows_StillFailsOpen_AndLogsTheExistingWarning()
    {
        var logger = new CapturingLogger();
        var config = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                Cors = new CorsConfig { AllowedOrigins = ["http://localhost:5005"] }
            }
        };

        var handler = new ApiKeyGatewayAuthHandler(
            config,
            logger,
            securityEvents: null,
            featureManager: new ThrowingFeatureManager());

        var result = await handler.AuthenticateAsync(
            CreateContext(new Dictionary<string, string> { ["Origin"] = "http://evil.example.com" }));

        result.IsAuthenticated.ShouldBeTrue("a flag evaluation fault must never lock the operator out.");
        logger.Warnings.ShouldContain(w => w.Contains("Failed to evaluate feature flag"));
    }

    [Fact]
    public async Task FlagOn_StillEnforcesOrigin()
    {
        // Non-vacuity: the guard must still be capable of rejecting, or the fail-open tests above
        // would pass against a permanently disabled guard.
        var config = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                Cors = new CorsConfig { AllowedOrigins = ["http://localhost:5005"] }
            },
            FeatureManagement = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
            {
                [FeatureFlags.GatewayDevOriginEnforcement] = JsonDocument.Parse("true").RootElement
            }
        };

        var handler = new ApiKeyGatewayAuthHandler(
            config,
            new CapturingLogger(),
            securityEvents: null,
            featureManager: new StubFeatureManager(enabled: true));

        var result = await handler.AuthenticateAsync(
            CreateContext(new Dictionary<string, string> { ["Origin"] = "http://evil.example.com" }));

        result.IsAuthenticated.ShouldBeFalse();
    }

    private static GatewayAuthContext CreateContext(IReadOnlyDictionary<string, string>? headers = null)
        => new()
        {
            Headers = headers ?? new Dictionary<string, string>(),
            QueryParameters = new Dictionary<string, string>(),
            Path = "/api/messages",
            Method = "POST"
        };

    private sealed class CapturingLogger : ILogger<ApiKeyGatewayAuthHandler>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                Warnings.Add(formatter(state, exception));
        }
    }

    private sealed class StubFeatureManager(bool enabled) : IFeatureManager
    {
        public async IAsyncEnumerable<string> GetFeatureNamesAsync()
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<bool> IsEnabledAsync(string feature) => Task.FromResult(enabled);

        public Task<bool> IsEnabledAsync<TContext>(string feature, TContext context) => Task.FromResult(enabled);
    }

    private sealed class ThrowingFeatureManager : IFeatureManager
    {
        public async IAsyncEnumerable<string> GetFeatureNamesAsync()
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<bool> IsEnabledAsync(string feature) => throw new InvalidOperationException("boom");

        public Task<bool> IsEnabledAsync<TContext>(string feature, TContext context)
            => throw new InvalidOperationException("boom");
    }
}
