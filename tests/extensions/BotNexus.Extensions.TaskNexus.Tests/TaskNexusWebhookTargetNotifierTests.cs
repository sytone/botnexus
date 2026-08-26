using System.Net;
using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Contracts.Webhooks;
using Microsoft.Extensions.Configuration;

namespace BotNexus.Extensions.TaskNexus.Tests;

/// <summary>
/// #3523: this notifier replaces a hand-run setup script. Its two load-bearing properties are
/// that it is completely inert when unconfigured, and that a downstream failure is absorbed
/// rather than propagated - the provisioner's startup pass is the recovery path.
/// </summary>
public sealed class TaskNexusWebhookTargetNotifierTests
{
    private static AgentWebhookBinding Binding(string agentId = "agent-a", string displayName = "Agent A")
        => new(AgentId.From(agentId), displayName, "wh_abc", $"/api/webhooks/{agentId}/wh_abc", "whsec_secret");

    private static IConfiguration Config(params (string Key, string Value)[] values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    // ── Inert when unconfigured (AC8) ─────────────────────────────────────────

    [Fact]
    public async Task NotifyAsync_WithNoBaseUrl_MakesNoOutboundCall()
    {
        var handler = new RecordingHandler();
        var notifier = new TaskNexusWebhookTargetNotifier(new HttpClient(handler), Config());

        await notifier.NotifyAsync(Binding(), CancellationToken.None);

        notifier.IsConfigured.ShouldBeFalse();
        // Zero handler invocations: a gateway with no TaskNexus deployment must not attempt a
        // single outbound request, not even one that fails fast.
        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task NotifyRemovedAsync_WithNoBaseUrl_MakesNoOutboundCall()
    {
        var handler = new RecordingHandler();
        var notifier = new TaskNexusWebhookTargetNotifier(new HttpClient(handler), Config());

        await notifier.NotifyRemovedAsync(AgentId.From("agent-a"), "wh_abc", CancellationToken.None);

        handler.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task NotifyAsync_WithBlankBaseUrl_IsTreatedAsUnconfigured(string baseUrl)
    {
        var handler = new RecordingHandler();
        var notifier = new TaskNexusWebhookTargetNotifier(
            new HttpClient(handler), Config((TaskNexusWebhookTargetNotifier.BaseUrlKey, baseUrl)));

        await notifier.NotifyAsync(Binding(), CancellationToken.None);

        notifier.IsConfigured.ShouldBeFalse();
        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task NotifyAsync_WithNullConfiguration_DoesNotThrow()
    {
        var handler = new RecordingHandler();
        var notifier = new TaskNexusWebhookTargetNotifier(new HttpClient(handler), configuration: null);

        await notifier.NotifyAsync(Binding(), CancellationToken.None);

        handler.Requests.ShouldBeEmpty();
    }

    // ── Configured delivery ───────────────────────────────────────────────────

    [Fact]
    public async Task NotifyAsync_WhenConfigured_PostsBindingPayload()
    {
        var handler = new RecordingHandler();
        var notifier = new TaskNexusWebhookTargetNotifier(
            new HttpClient(handler),
            Config((TaskNexusWebhookTargetNotifier.BaseUrlKey, "https://tasknexus.example.com")));

        await notifier.NotifyAsync(Binding(displayName: "Renamed"), CancellationToken.None);

        var request = handler.Requests.ShouldHaveSingleItem();
        request.Method.ShouldBe(HttpMethod.Post);
        request.Uri.ShouldBe("https://tasknexus.example.com/api/botnexus/agents");

        using var payload = JsonDocument.Parse(request.Body!);
        var root = payload.RootElement;
        root.GetProperty("agentId").GetString().ShouldBe("agent-a");
        root.GetProperty("displayName").GetString().ShouldBe("Renamed");
        root.GetProperty("webhookId").GetString().ShouldBe("wh_abc");
        root.GetProperty("secret").GetString().ShouldBe("whsec_secret");
    }

    [Fact]
    public async Task NotifyAsync_WithTrailingSlashInBaseUrl_DoesNotDoubleTheSeparator()
    {
        var handler = new RecordingHandler();
        var notifier = new TaskNexusWebhookTargetNotifier(
            new HttpClient(handler),
            Config((TaskNexusWebhookTargetNotifier.BaseUrlKey, "https://tasknexus.example.com/")));

        await notifier.NotifyAsync(Binding(), CancellationToken.None);

        handler.Requests.ShouldHaveSingleItem().Uri
            .ShouldBe("https://tasknexus.example.com/api/botnexus/agents");
    }

    [Fact]
    public async Task NotifyAsync_WithCallbackOrigin_SendsAbsoluteUrl()
    {
        var handler = new RecordingHandler();
        var notifier = new TaskNexusWebhookTargetNotifier(
            new HttpClient(handler),
            Config(
                (TaskNexusWebhookTargetNotifier.BaseUrlKey, "https://tasknexus.example.com"),
                (TaskNexusWebhookTargetNotifier.CallbackOriginKey, "https://gateway.example.com")));

        await notifier.NotifyAsync(Binding(), CancellationToken.None);

        using var payload = JsonDocument.Parse(handler.Requests.Single().Body!);
        // The binding carries a RELATIVE path because the gateway does not reliably know its own
        // public origin; the target composes the absolute URL from its own configured base.
        payload.RootElement.GetProperty("url").GetString()
            .ShouldBe("https://gateway.example.com/api/webhooks/agent-a/wh_abc");
    }

    [Fact]
    public async Task NotifyAsync_WithoutCallbackOrigin_SendsTheRelativePath()
    {
        var handler = new RecordingHandler();
        var notifier = new TaskNexusWebhookTargetNotifier(
            new HttpClient(handler),
            Config((TaskNexusWebhookTargetNotifier.BaseUrlKey, "https://tasknexus.example.com")));

        await notifier.NotifyAsync(Binding(), CancellationToken.None);

        using var payload = JsonDocument.Parse(handler.Requests.Single().Body!);
        payload.RootElement.GetProperty("url").GetString().ShouldBe("/api/webhooks/agent-a/wh_abc");
    }

    [Fact]
    public async Task NotifyRemovedAsync_WhenConfigured_DeletesTheExactRegistration()
    {
        var handler = new RecordingHandler();
        var notifier = new TaskNexusWebhookTargetNotifier(
            new HttpClient(handler),
            Config((TaskNexusWebhookTargetNotifier.BaseUrlKey, "https://tasknexus.example.com")));

        await notifier.NotifyRemovedAsync(AgentId.From("agent-a"), "wh_abc", CancellationToken.None);

        var request = handler.Requests.ShouldHaveSingleItem();
        request.Method.ShouldBe(HttpMethod.Delete);
        // The webhook id MUST be on the wire. TaskNexus deletes WHERE agent = ? AND webhook_id = ?
        // and treats a mismatch as an idempotent false; without the id a delayed or retried DELETE
        // for a since-deleted agent erases the NEWER binding of a recreated agent with the same id.
        request.Uri.ShouldBe("https://tasknexus.example.com/api/botnexus/agents/agent-a/wh_abc");
    }

    [Fact]
    public async Task NotifyRemovedAsync_EscapesTheIdentifiersItPutsInTheUri()
    {
        var handler = new RecordingHandler();
        var notifier = new TaskNexusWebhookTargetNotifier(
            new HttpClient(handler),
            Config((TaskNexusWebhookTargetNotifier.BaseUrlKey, "https://tasknexus.example.com")));

        await notifier.NotifyRemovedAsync(AgentId.From("agent a"), "wh/ab c", CancellationToken.None);

        // An unescaped id would silently change the path shape TaskNexus matches on, which
        // for a conditional delete means matching the wrong row or none at all.
        handler.Requests.ShouldHaveSingleItem().Uri
            .ShouldBe("https://tasknexus.example.com/api/botnexus/agents/agent%20a/wh%2Fab%20c");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task NotifyRemovedAsync_WithBlankWebhookId_ThrowsAndMakesNoOutboundCall(string webhookId)
    {
        var handler = new RecordingHandler();
        var notifier = new TaskNexusWebhookTargetNotifier(
            new HttpClient(handler),
            Config((TaskNexusWebhookTargetNotifier.BaseUrlKey, "https://tasknexus.example.com")));

        // Fail closed: a blank id would degrade the conditional delete back into the unsafe
        // agent-id-only delete this contract exists to prevent.
        await Should.ThrowAsync<ArgumentException>(
            () => notifier.NotifyRemovedAsync(AgentId.From("agent-a"), webhookId, CancellationToken.None));
        handler.Requests.ShouldBeEmpty();
    }

    // ── Failure absorption ────────────────────────────────────────────────────

    [Fact]
    public async Task NotifyAsync_WhenTargetReturnsError_DoesNotThrow()
    {
        var handler = new RecordingHandler { StatusCode = HttpStatusCode.InternalServerError };
        var notifier = new TaskNexusWebhookTargetNotifier(
            new HttpClient(handler),
            Config((TaskNexusWebhookTargetNotifier.BaseUrlKey, "https://tasknexus.example.com")));

        // A rejected push must not surface as a failed agent create. There is no retry or outbox
        // by design: the provisioner's startup reconciliation re-sends every binding.
        await Should.NotThrowAsync(() => notifier.NotifyAsync(Binding(), CancellationToken.None));
    }

    [Fact]
    public async Task NotifyAsync_WhenTargetIsUnreachable_DoesNotThrow()
    {
        var handler = new RecordingHandler { Throw = new HttpRequestException("connection refused") };
        var notifier = new TaskNexusWebhookTargetNotifier(
            new HttpClient(handler),
            Config((TaskNexusWebhookTargetNotifier.BaseUrlKey, "https://tasknexus.example.com")));

        await Should.NotThrowAsync(() => notifier.NotifyAsync(Binding(), CancellationToken.None));
    }

    [Fact]
    public async Task NotifyRemovedAsync_WhenTargetIsUnreachable_DoesNotThrow()
    {
        var handler = new RecordingHandler { Throw = new HttpRequestException("connection refused") };
        var notifier = new TaskNexusWebhookTargetNotifier(
            new HttpClient(handler),
            Config((TaskNexusWebhookTargetNotifier.BaseUrlKey, "https://tasknexus.example.com")));

        // Especially important on the delete path, which the controller already treats as
        // best-effort: a TaskNexus outage must never block deleting an agent.
        await Should.NotThrowAsync(
            () => notifier.NotifyRemovedAsync(AgentId.From("agent-a"), "wh_abc", CancellationToken.None));
    }

    private sealed record CapturedRequest(HttpMethod Method, string Uri, string? Body);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];
        public HttpStatusCode StatusCode { get; init; } = HttpStatusCode.OK;
        public Exception? Throw { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            // GetComponents with UriEscaped preserves every escaped path segment. AbsoluteUri
            // normalises spaces back to literal spaces, which makes a correct %20 look broken.
            var uri = request.RequestUri!.GetComponents(UriComponents.AbsoluteUri, UriFormat.UriEscaped);
            Requests.Add(new CapturedRequest(request.Method, uri, body));

            if (Throw is not null)
                throw Throw;

            return new HttpResponseMessage(StatusCode);
        }
    }
}
