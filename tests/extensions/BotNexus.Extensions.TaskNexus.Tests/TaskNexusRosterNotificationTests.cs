using System.Net;
using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Contracts.Webhooks;
using Microsoft.Extensions.Configuration;

namespace BotNexus.Extensions.TaskNexus.Tests;

/// <summary>#3714 RED tests for the secret-free TaskNexus roster wire contract.</summary>
public sealed class TaskNexusRosterNotificationTests
{
    [Fact]
    public async Task NotifyRosterSucceededAsync_PostsOnlySuccessObservedAtAndAgents()
    {
        var at = new DateTimeOffset(2026, 9, 15, 12, 34, 56, TimeSpan.Zero);
        var handler = new RecordingHandler();

        await Notifier(handler).NotifyRosterSucceededAsync(
            [new AgentRosterEntry(AgentId.From("agent-a"), "Agent A")], at,
            CancellationToken.None);

        var request = handler.Requests.ShouldHaveSingleItem();
        request.Method.ShouldBe(HttpMethod.Post);
        request.Uri.ShouldBe("https://tasknexus.example.com/api/botnexus/roster");
        using var payload = JsonDocument.Parse(request.Body);
        var root = payload.RootElement;
        Names(root).ShouldBe(["agents", "observedAt", "status"]);
        root.GetProperty("status").GetString().ShouldBe("success");
        root.GetProperty("observedAt").GetDateTimeOffset().ShouldBe(at);
        var agent = root.GetProperty("agents").EnumerateArray().ShouldHaveSingleItem();
        Names(agent).ShouldBe(["agentId", "displayName"]);
        agent.GetProperty("agentId").GetString().ShouldBe("agent-a");
        agent.GetProperty("displayName").GetString().ShouldBe("Agent A");
    }

    [Fact]
    public async Task NotifyRosterSucceededAsync_EmptyRoster_PostsEmptyAgentsArray()
    {
        var handler = new RecordingHandler();

        await Notifier(handler).NotifyRosterSucceededAsync(
            [], DateTimeOffset.UnixEpoch, CancellationToken.None);

        using var payload = JsonDocument.Parse(handler.Requests.ShouldHaveSingleItem().Body);
        payload.RootElement.GetProperty("agents").GetArrayLength().ShouldBe(0);
        Names(payload.RootElement).ShouldBe(["agents", "observedAt", "status"]);
    }

    [Fact]
    public async Task NotifyRosterFailedAsync_PostsOnlyFailureObservedAtAndBoundedErrorCode()
    {
        const string secret = "whsec-super-secret";
        const string privateUrl = "https://gateway.example/hook?token=do-not-leak";
        var at = new DateTimeOffset(2026, 9, 15, 12, 34, 56, TimeSpan.Zero);
        var handler = new RecordingHandler();

        await Notifier(handler, privateUrl).NotifyRosterFailedAsync(
            "roster_reconciliation_failed", at, CancellationToken.None);

        var request = handler.Requests.ShouldHaveSingleItem();
        request.Uri.ShouldBe("https://tasknexus.example.com/api/botnexus/roster");
        using var payload = JsonDocument.Parse(request.Body);
        var root = payload.RootElement;
        Names(root).ShouldBe(["errorCode", "observedAt", "status"]);
        root.GetProperty("status").GetString().ShouldBe("failure");
        root.GetProperty("observedAt").GetDateTimeOffset().ShouldBe(at);
        root.GetProperty("errorCode").GetString().ShouldBe("roster_reconciliation_failed");
        request.Body.ShouldNotContain(secret);
        request.Body.ShouldNotContain(privateUrl);
        request.Body.ShouldNotContain("url", Case.Sensitive);
    }

    [Theory]
    [InlineData("SqlException: password=do-not-leak")]
    [InlineData("https://gateway.example/hook?token=do-not-leak")]
    [InlineData("whsec-super-secret")]
    public async Task NotifyRosterFailedAsync_RejectsRawDiagnosticData(string rawDiagnostic)
    {
        var handler = new RecordingHandler();

        await Should.ThrowAsync<ArgumentException>(() => Notifier(handler).NotifyRosterFailedAsync(
            rawDiagnostic, DateTimeOffset.UnixEpoch, CancellationToken.None));

        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task RosterNotifications_Unconfigured_AreInert()
    {
        var handler = new RecordingHandler();
        var notifier = new TaskNexusWebhookTargetNotifier(new HttpClient(handler), Config());

        await notifier.NotifyRosterSucceededAsync(
            [new AgentRosterEntry(AgentId.From("agent-a"), "Agent A")],
            DateTimeOffset.UnixEpoch, CancellationToken.None);
        await notifier.NotifyRosterFailedAsync(
            "roster_reconciliation_failed", DateTimeOffset.UnixEpoch, CancellationToken.None);

        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task RosterNotifications_TaskNexusOutage_IsAbsorbed()
    {
        var unreachable = new RecordingHandler
        {
            Throw = new HttpRequestException("connection refused: https://private.example/token")
        };
        await Should.NotThrowAsync(() => Notifier(unreachable).NotifyRosterSucceededAsync(
            [new AgentRosterEntry(AgentId.From("agent-a"), "Agent A")],
            DateTimeOffset.UnixEpoch, CancellationToken.None));

        var rejected = new RecordingHandler { StatusCode = HttpStatusCode.ServiceUnavailable };
        await Should.NotThrowAsync(() => Notifier(rejected).NotifyRosterFailedAsync(
            "roster_reconciliation_failed", DateTimeOffset.UnixEpoch, CancellationToken.None));
    }

    private static TaskNexusWebhookTargetNotifier Notifier(
        RecordingHandler handler, string? callbackOrigin = null)
    {
        var values = new List<(string Key, string Value)>
        {
            (TaskNexusWebhookTargetNotifier.BaseUrlKey, "https://tasknexus.example.com")
        };
        if (callbackOrigin is not null)
            values.Add((TaskNexusWebhookTargetNotifier.CallbackOriginKey, callbackOrigin));
        return new TaskNexusWebhookTargetNotifier(new HttpClient(handler), Config([.. values]));
    }

    private static IConfiguration Config(params (string Key, string Value)[] values)
        => new ConfigurationBuilder().AddInMemoryCollection(values.Select(value =>
            new KeyValuePair<string, string?>(value.Key, value.Value))).Build();

    private static string[] Names(JsonElement element)
        => element.EnumerateObject().Select(property => property.Name).OrderBy(name => name).ToArray();

    private sealed record CapturedRequest(HttpMethod Method, string Uri, string Body);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];
        public HttpStatusCode StatusCode { get; init; } = HttpStatusCode.OK;
        public Exception? Throw { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            var uri = request.RequestUri?.GetComponents(
                UriComponents.AbsoluteUri, UriFormat.UriEscaped) ?? string.Empty;
            Requests.Add(new CapturedRequest(request.Method, uri, body));
            if (Throw is not null) throw Throw;
            return new HttpResponseMessage(StatusCode);
        }
    }
}
