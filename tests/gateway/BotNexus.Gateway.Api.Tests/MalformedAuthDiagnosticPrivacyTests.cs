using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using BotNexus.Agent.Providers.Copilot.Discovery;
using BotNexus.Agent.Providers.Copilot.Headers;
using BotNexus.Cron;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using System.IO.Abstractions.TestingHelpers;

namespace BotNexus.Gateway.Api.Tests;

public sealed partial class CopilotQuotaControllerTests
{
    private const string PrivateAuthProperty = "private-auth-property-4811-91ac32";
    private const string PrivateAuthValue = "private-auth-value-4811-629db4";
    private const string PrivatePartialCredential = "partial-credential-4811-c2a859";
    private const string FixedMalformedAuthWarning = "Failed to parse auth file.";
    // A complete entry precedes the invalid entry: deserialization must not retain partial credentials.
    private const string MalformedAuthJson = "{\"github-copilot\":{\"refresh\":\"" + PrivatePartialCredential +
        "\"},\"" + PrivateAuthProperty + "\":{\"expires\":\"" + PrivateAuthValue + "\"}}";

    public static IEnumerable<object[]> MalformedAuthDiagnosticCases()
    {
        foreach (var route in new[] { "quota-get", "details-get", "quota-refresh" })
        foreach (var legacy in new[] { false, true })
        foreach (var surface in new[] { "formatted-message", "structured-state", "exception", "fixed-warning" })
            yield return [route, legacy, surface];
    }

    [Theory]
    [MemberData(nameof(MalformedAuthDiagnosticCases))]
    public async Task MalformedAuth_Diagnostics_ExcludePrivateDataOnEveryLoggingSurface(string route, bool legacy, string surface)
    {
        var fixture = MalformedAuthFixture(legacy);
        var result = await InvokeMalformedAuthRoute(fixture, route);
        AssertMalformedAuthUnavailable(result);
        fixture.Handler.Requests.ShouldBeEmpty();
        AssertNoAuthDiagnostics(JsonSerializer.Serialize(result));
        var warning = fixture.Logger.Entries.ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        // Separate cases keep an early message failure from hiding exception/state disclosure.
        switch (surface)
        {
            case "formatted-message":
                AssertNoAuthDiagnostics(warning.Message, fixture.Path);
                break;
            case "structured-state":
                AssertNoAuthDiagnostics(warning.StateText, fixture.Path);
                foreach (var pair in warning.Properties)
                {
                    AssertNoAuthDiagnostics(pair.Key, fixture.Path);
                    AssertNoAuthDiagnostics(pair.Value?.ToString() ?? "", fixture.Path);
                }
                warning.Properties.ShouldNotContain(pair => pair.Key == "AuthPath");
                break;
            case "exception":
                AssertNoAuthDiagnostics(warning.Exception?.ToString() ?? "", fixture.Path);
                warning.Exception.ShouldBeNull();
                break;
            case "fixed-warning":
                warning.Message.ShouldBe(FixedMalformedAuthWarning);
                warning.Properties.Single(pair => pair.Key == "{OriginalFormat}").Value.ShouldBe(FixedMalformedAuthWarning);
                break;
            default:
                throw new InvalidOperationException("Unexpected logging surface.");
        }
    }

    [Fact]
    public void MalformedAuthFixture_InvalidPrivateProperty_IsPresentInJsonExceptionPath()
    {
        // Verify the fixture is not just a token sentinel that never reaches the parser diagnostic.
        // This mirrors the relevant auth entry types without accessing production internals.
        var error = Should.Throw<JsonException>(() => JsonSerializer.Deserialize<Dictionary<string, AuthDiagnosticProbe>>(MalformedAuthJson));
        (error.Path ?? "").ShouldContain(PrivateAuthProperty);
        error.Message.ShouldContain(PrivateAuthProperty);
    }

    [Theory]
    [InlineData("quota-get", false)]
    [InlineData("quota-get", true)]
    [InlineData("details-get", false)]
    [InlineData("details-get", true)]
    [InlineData("quota-refresh", false)]
    [InlineData("quota-refresh", true)]
    public async Task MalformedAuth_RepeatedReadsFailClosed_CorrectedFileReloadsWithoutRestart(string route, bool legacy)
    {
        var fixture = MalformedAuthFixture(legacy);
        AssertMalformedAuthUnavailable(await InvokeMalformedAuthRoute(fixture, route));
        AssertMalformedAuthUnavailable(await InvokeMalformedAuthRoute(fixture, route));
        fixture.Logger.Entries.Count.ShouldBe(1); // unchanged malformed files are cached, not repeatedly parsed
        (await fixture.Auth.GetCopilotOAuthTokenAsync()).ShouldBeNull();
        fixture.Handler.Requests.ShouldBeEmpty();

        fixture.Fs.File.WriteAllText(fixture.Path, "{\"github-copilot\":{\"refresh\":\"corrected-default-credential-4811\"},\"second-auth\":{\"refresh\":\"corrected-second-credential-4811\"}}");
        (await fixture.Auth.GetCopilotOAuthTokenAsync()).ShouldBe("corrected-default-credential-4811");
        fixture.Handler.Requests.ShouldBeEmpty();
        var refreshed = await fixture.Account.RefreshAsync();
        refreshed.Snapshots.ShouldHaveSingleItem().Remaining.ShouldBe(11m);
        var localQuota = (await AuthorizedController(fixture.Account).GetQuota(CancellationToken.None)).ShouldBeOfType<OkObjectResult>().Value.ShouldBeOfType<CopilotQuotaState>();
        localQuota.Snapshots.ShouldHaveSingleItem().Remaining.ShouldBe(11m);
        var details = (await DetailsController(fixture.Details).GetDetails(CancellationToken.None)).ShouldBeOfType<OkObjectResult>().Value.ShouldBeOfType<ProviderUsageDetails>();
        details.Account.State.ShouldBe("fresh");
        details.Account.Snapshots.ShouldHaveSingleItem().Remaining.ShouldBe(11m);
        fixture.Handler.Requests.Count.ShouldBe(1);
        fixture.Logger.Entries.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MalformedAuth_AfterCachedSuccess_ClearsQuotaAndHeadersUntilCorrected(bool legacy)
    {
        var fixture = MalformedAuthFixture(legacy);
        fixture.Fs.File.WriteAllText(fixture.Path, "{\"github-copilot\":{\"refresh\":\"previous-credential-4811\"}}");
        (await fixture.Account.RefreshAsync()).Snapshots.ShouldHaveSingleItem();
        var credential = fixture.Auth.ResolveCopilotAccountCredential("github-copilot") ?? throw new InvalidOperationException();
        fixture.Headers.Observe(new(new(credential.Instance, credential.Generation), CopilotQuotaDimension.Chat, 1, DateTimeOffset.UtcNow,
            new(100m, 50m, 50m, 0m, false, null, false)));
        (await fixture.Details.ReadAsync()).Headers.ShouldHaveSingleItem();

        fixture.Fs.File.WriteAllText(fixture.Path, MalformedAuthJson);
        AssertMalformedAuthUnavailable(await InvokeMalformedAuthRoute(fixture, "quota-get"));
        AssertMalformedAuthUnavailable(await InvokeMalformedAuthRoute(fixture, "details-get"));
        AssertMalformedAuthUnavailable(await InvokeMalformedAuthRoute(fixture, "quota-refresh"));
        fixture.Handler.Requests.Count.ShouldBe(1);
        fixture.Logger.Entries.Count.ShouldBe(1);
        fixture.Fs.File.WriteAllText(fixture.Path, "{\"github-copilot\":{\"refresh\":\"replacement-longer-credential-4811\"}}");
        (await fixture.Account.ReadAsync()).Snapshots.ShouldBeEmpty();
        (await fixture.Details.ReadAsync()).Headers.ShouldBeEmpty();
        (await fixture.Account.RefreshAsync()).Snapshots.ShouldHaveSingleItem();
        fixture.Handler.Requests.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData("quota-get", false)]
    [InlineData("quota-get", true)]
    [InlineData("details-get", false)]
    [InlineData("details-get", true)]
    [InlineData("quota-refresh", false)]
    [InlineData("quota-refresh", true)]
    public async Task MalformedAuth_PreCancelledRequest_PropagatesCancellationBeforeParsingOrUpstream(string route, bool legacy)
    {
        var fixture = MalformedAuthFixture(legacy);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => InvokeMalformedAuthRoute(fixture, route, token: cancelled.Token));
        fixture.Logger.Entries.ShouldBeEmpty();
        fixture.Handler.Requests.ShouldBeEmpty();
        await fixture.Cron.DidNotReceiveWithAnyArgs().GetRunActivityAsync(default, default);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MalformedAuth_NamedInstances_StayUnavailableAndRecoverWithoutDefaultFallback(bool legacy)
    {
        var fixture = MalformedAuthFixture(legacy);
        foreach (var instance in new[] { "github-copilot", "second", "unknown" })
        foreach (var route in new[] { "quota-get", "details-get", "quota-refresh" })
            AssertMalformedAuthUnavailable(await InvokeMalformedAuthRoute(fixture, route, instance));
        fixture.Handler.Requests.ShouldBeEmpty();
        fixture.Logger.Entries.Count.ShouldBe(1);
        fixture.Fs.File.WriteAllText(fixture.Path, "{\"github-copilot\":{\"refresh\":\"corrected-default-credential-4811\"},\"second-auth\":{\"refresh\":\"corrected-second-credential-4811\"}}");
        (await fixture.Account.RefreshAsync()).Snapshots.ShouldHaveSingleItem().Remaining.ShouldBe(11m);
        (await fixture.Account.ReadAsync("second")).Snapshots.ShouldBeEmpty();
        (await fixture.Account.RefreshAsync("second")).Snapshots.ShouldHaveSingleItem().Remaining.ShouldBe(22m);
        (await fixture.Account.ReadAsync()).Snapshots.ShouldHaveSingleItem().Remaining.ShouldBe(11m);
        AssertMalformedAuthUnavailable(await InvokeMalformedAuthRoute(fixture, "quota-refresh", "unknown"));
        fixture.Handler.Requests.Count.ShouldBe(2);
        fixture.Handler.Requests.Select(request => request.AuthorizationParameter).ShouldBe(new[] { "corrected-default-credential-4811", "corrected-second-credential-4811" });
    }

    private static async Task<object> InvokeMalformedAuthRoute(AuthPrivacyFixture fixture, string route, string instance = "github-copilot", CancellationToken token = default)
    {
        var result = route switch
        {
            "quota-get" => await AuthorizedController(fixture.Account).GetQuota(token, instance),
            "quota-refresh" => await AuthorizedController(fixture.Account).RefreshQuota(token, instance),
            "details-get" => await DetailsController(fixture.Details).GetDetails(token, instance),
            _ => throw new InvalidOperationException("Unexpected route.")
        };
        return result.ShouldBeOfType<OkObjectResult>().Value ?? throw new InvalidOperationException("Missing response.");
    }

    private static void AssertMalformedAuthUnavailable(object result)
    {
        if (result is CopilotQuotaState quota)
        {
            quota.Instance.ShouldBeNull();
            quota.AttemptState.ShouldBe("unavailable");
            quota.Snapshots.ShouldBeEmpty();
            quota.LastSuccessAtUtc.ShouldBeNull();
        }
        else
        {
            var details = result.ShouldBeOfType<ProviderUsageDetails>();
            details.Instance.ShouldBeNull();
            details.Account.State.ShouldBe("unavailable");
            details.Account.Snapshots.ShouldBeEmpty();
            details.Headers.ShouldBeEmpty();
        }
    }

    private static void AssertNoAuthDiagnostics(string text, string? path = null)
    {
        foreach (var sentinel in new[] { PrivateAuthProperty, PrivateAuthValue, PrivatePartialCredential, "JsonException", "LineNumber", "BytePositionInLine", "$." })
            text.ShouldNotContain(sentinel);
        if (path is not null) text.ShouldNotContain(path);
        text.ShouldNotContain("auth.json");
        text.ShouldNotContain(".botnexus-agent");
    }

    private static AuthPrivacyFixture MalformedAuthFixture(bool legacy)
    {
        var fs = new MockFileSystem();
        var path = legacy ? Path.Combine(Environment.CurrentDirectory, ".botnexus-agent", "auth.json") :
            Path.Combine(PlatformConfigLoader.GetDefaultConfigDirectory(fs), "auth.json");
        fs.Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new InvalidOperationException());
        fs.File.WriteAllText(path, MalformedAuthJson);
        var options = Substitute.For<IOptionsMonitor<PlatformConfig>>();
        options.CurrentValue.Returns(new PlatformConfig { Providers = new()
        {
            ["github-copilot"] = new(), ["second"] = new() { Type = "github-copilot", ApiKey = "auth:second-auth" }
        } });
        var logger = new AuthDiagnosticLogger();
        var auth = new GatewayAuthManager(options, logger, fs);
        var handler = new RecordingHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(request.Headers.Authorization?.Parameter == "corrected-second-credential-4811" ?
                "{\"quota_snapshots\":{\"chat\":{\"quota_remaining\":22}}}" : "{\"quota_snapshots\":{\"chat\":{\"quota_remaining\":11}}}")
        });
        var account = new CopilotQuotaService(auth, new CopilotDiscoveryClient(new HttpClient(handler)), NullLogger<CopilotQuotaService>.Instance);
        var cron = Substitute.For<ICronStore>();
        cron.GetRunActivityAsync(Arg.Any<CronRunActivityQuery>(), Arg.Any<CancellationToken>()).Returns(call => EmptyActivity(call.Arg<CronRunActivityQuery>()));
        var headers = new CopilotHeaderQuotaStore();
        return new(fs, path, auth, logger, handler, account, new(auth, account, headers, cron, options), headers, cron);
    }

    private sealed record AuthPrivacyFixture(MockFileSystem Fs, string Path, GatewayAuthManager Auth, AuthDiagnosticLogger Logger,
        RecordingHandler Handler, CopilotQuotaService Account, ProviderUsageDetailsService Details, CopilotHeaderQuotaStore Headers, ICronStore Cron);

    private sealed class AuthDiagnosticProbe
    {
        [JsonPropertyName("refresh")]
        public string Refresh { get; set; } = "";
        [JsonPropertyName("expires")]
        public long Expires { get; set; }
    }

    private sealed class AuthDiagnosticLogger : ILogger<GatewayAuthManager>
    {
        public List<AuthDiagnosticRecord> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            // Preserve all three channels; formatting alone hides exception and structured-state leaks.
            var properties = state is IEnumerable<KeyValuePair<string, object?>> pairs ? pairs.ToArray() : [];
            Entries.Add(new(logLevel, formatter(state, exception), state?.ToString() ?? "", properties, exception));
        }
    }

    private sealed record AuthDiagnosticRecord(LogLevel Level, string Message, string StateText,
        IReadOnlyList<KeyValuePair<string, object?>> Properties, Exception? Exception);
}
