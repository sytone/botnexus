using System.Net;
using System.Text;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class GatewayInfoServiceTests
{
    [Fact]
    public async Task LoadAsync_PopulatesConfiguredDefaultAgentId()
    {
        var service = CreateService("""{"startedAt":"2026-01-01T00:00:00Z","uptimeSeconds":1,"commitSha":"abc","commitShort":"abc","version":"1.0.0","defaultAgentId":"configured-agent"}""");

        await service.LoadAsync();

        service.Info.ShouldNotBeNull().DefaultAgentId.ShouldBe("configured-agent");
    }

    [Fact]
    public async Task LoadAsync_AllowsAbsentDefaultAgentId_ForCompatibility()
    {
        var service = CreateService("""{"startedAt":"2026-01-01T00:00:00Z","uptimeSeconds":1,"commitSha":"abc","commitShort":"abc","version":"1.0.0"}""");

        await service.LoadAsync();

        service.Info.ShouldNotBeNull().DefaultAgentId.ShouldBeNull();
    }

    private static GatewayInfoService CreateService(string json)
    {
        var handler = new StubHandler(json);
        var restClient = Substitute.For<IGatewayRestClient>();
        restClient.ApiBaseUrl.Returns("http://gateway/api/");
        return new GatewayInfoService(new HttpClient(handler), restClient);
    }

    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
    }
}
