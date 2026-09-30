using System.Net;
using System.Text;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class ClientBuildInfoProjectionTests
{
    [Fact]
    public async Task LoadAsync_projects_one_shared_client_build_identity()
    {
        var rest = Substitute.For<IGatewayRestClient>();
        rest.ApiBaseUrl.Returns("https://example.test/api/");
        using var http = new HttpClient(new JsonHandler("""
            {"commitSha":"abcdef1234567890","commitShort":"abcdef1","buildTimestamp":"2026-09-29T04:00:00Z"}
            """));
        var service = new GatewayInfoService(http, rest);

        await service.LoadAsync();

        service.Info.ShouldNotBeNull();
        service.Info.CommitShort.ShouldBe("abcdef1");
        service.Info.BuildTimestamp.ShouldBe(DateTimeOffset.Parse("2026-09-29T04:00:00Z"));
        GatewayInfoService.FormatBuildIdentity(service.Info).ShouldBe("abcdef1 · 2026-09-29 04:00 UTC");
    }

    private sealed class JsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.RequestUri.ShouldBe(new Uri("https://example.test/api/gateway/info"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }
}
