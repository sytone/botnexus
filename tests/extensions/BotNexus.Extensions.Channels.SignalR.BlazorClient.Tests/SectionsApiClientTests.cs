using System.Net;
using System.Text;
using System.Text.Json;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class SectionsApiClientTests
{
    [Theory]
    [InlineData("{\"sections\":[],\"assignments\":{}}")]
    [InlineData("{\"sections\":[],\"assignments\":{},\"groupOrder\":null}")]
    public async Task ListAsync_LegacySuccess_IsEmptySnapshotNotUnavailable(string body)
    {
        using var http = Client(new Handler(_ => Response(body)));
        var result = await new SectionsApiClient(http).ListAsync("a");
        Assert.Empty(result.Sections);
        Assert.Null(result.GroupOrder);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task ListAsync_Failure_DoesNotReturnEmpty(HttpStatusCode status)
    {
        using var http = Client(new Handler(_ => Response("{}", status)));
        var error = await Assert.ThrowsAsync<SectionsApiException>(() => new SectionsApiClient(http).ListAsync("a"));
        Assert.Equal(status, error.StatusCode);
        Assert.False(error.OutcomeUnknown);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"assignments\":{}}")]
    [InlineData("{\"sections\":[]}")]
    [InlineData("{\"groupOrder\":[]}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"sections\":null,\"assignments\":{}}")]
    [InlineData("{\"sections\":[],\"assignments\":null}")]
    public async Task ListAsync_InvalidPayload_IsUnavailable(string body)
    {
        using var http = Client(new Handler(_ => Response(body)));
        await Assert.ThrowsAsync<SectionsApiException>(() => new SectionsApiClient(http).ListAsync("a"));
    }

    [Fact]
    public async Task ReorderGroupsAsync_UsesDisjointNamedWireEntries_AndCanonicalResponse()
    {
        string? payload = null;
        using var http = Client(new Handler(async request =>
        {
            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.Equal("/api/agents/a/sections/group-order", request.RequestUri?.AbsolutePath);
            payload = request.Content is null ? null : await request.Content.ReadAsStringAsync();
            return await Response("[{\"system\":\"Webhooks\"},{\"sectionId\":\"s\"},{\"system\":\"Pinned\"},{\"system\":\"Conversations\"},{\"system\":\"Scheduled\"}]");
        }));
        var result = await new SectionsApiClient(http).ReorderGroupsAsync("a", [new(System: BuiltInConversationSection.Webhooks), new(SectionId: "s")]);
        Assert.Equal(5, result.Count);
        Assert.Equal(BuiltInConversationSection.Webhooks, result[0].System);
        using var document = JsonDocument.Parse(payload ?? throw new InvalidOperationException("Missing request"));
        var entries = document.RootElement.GetProperty("groupOrder");
        Assert.Equal("Webhooks", entries[0].GetProperty("system").GetString());
        Assert.Single(entries[0].EnumerateObject());
        Assert.Single(entries[1].EnumerateObject());
        Assert.Equal("s", entries[1].GetProperty("sectionId").GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.MethodNotAllowed)]
    [InlineData(HttpStatusCode.NotImplemented)]
    public async Task ReorderGroupsAsync_OlderServer_ExplicitUnsupportedError(HttpStatusCode status)
    {
        using var http = Client(new Handler(_ => Response("{}", status)));
        var error = await Assert.ThrowsAsync<SectionsApiException>(() => new SectionsApiClient(http).ReorderGroupsAsync("a", []));
        Assert.True(error.UnsupportedGroupOrder);
        Assert.Contains("support", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_TransportFailure_IsUnknownNotSafeToRepost()
    {
        using var http = Client(new Handler(_ => throw new HttpRequestException("disconnected")));
        var error = await Assert.ThrowsAsync<SectionsApiException>(() => new SectionsApiClient(http).CreateAsync("a", "Work"));
        Assert.True(error.OutcomeUnknown);
        Assert.False(error.WriteConfirmed);
    }

    [Fact]
    public async Task CreateAsync_SuccessWithMalformedBody_WriteIsAlreadyConfirmed()
    {
        using var http = Client(new Handler(_ => Response("null", HttpStatusCode.Created)));
        var error = await Assert.ThrowsAsync<SectionsApiException>(() => new SectionsApiClient(http).CreateAsync("a", "Work"));
        Assert.True(error.WriteConfirmed);
    }

    [Theory]
    [InlineData("{\"sections\":[],\"assignments\":{},\"groupOrder\":[{\"system\":99}]}")]
    [InlineData("{\"sections\":[],\"assignments\":{},\"groupOrder\":[{\"system\":\"Unknown\"}]}")]
    public async Task ListAsync_InvalidSystemIdentity_IsNotEmptySuccess(string body)
    {
        using var http = Client(new Handler(_ => Response(body)));
        await Assert.ThrowsAsync<SectionsApiException>(() => new SectionsApiClient(http).ListAsync("a"));
    }

    [Fact]
    public async Task ListAsync_Cancellation_IsNotEmptySuccess()
    {
        using var http = Client(new Handler(_ => throw new OperationCanceledException()));
        var error = await Assert.ThrowsAsync<SectionsApiException>(() => new SectionsApiClient(http).ListAsync("a"));
        Assert.False(error.WriteConfirmed);
        Assert.False(error.OutcomeUnknown);
        Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
    }

    private static HttpClient Client(HttpMessageHandler handler) => new(handler) { BaseAddress = new Uri("http://localhost/") };
    private static Task<HttpResponseMessage> Response(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
