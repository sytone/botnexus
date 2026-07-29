using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Shared bUnit setup helper for the <see cref="NavOrderApiClient"/> dependency that
/// <c>MainLayout</c> injects (#2236). Any test context that renders <c>MainLayout</c> must
/// register this service or the render throws
/// <see cref="System.InvalidOperationException"/> ("no registered service of type
/// NavOrderApiClient"). Registering it here keeps every MainLayout-rendering test context in
/// sync rather than duplicating the wiring per test class.
/// </summary>
internal static class NavOrderTestSetup
{
    /// <summary>
    /// Registers a <see cref="NavOrderApiClient"/> backed by a stub handler that returns the
    /// built-in effective nav order for <c>GET /api/nav-order</c>. The client swallows failures
    /// and returns an empty list, so the stub simply keeps renders deterministic.
    /// </summary>
    public static IServiceCollection AddStubNavOrderApiClient(this IServiceCollection services)
    {
        var http = new HttpClient(new StubNavOrderHandler()) { BaseAddress = new Uri("http://localhost/") };
        services.AddSingleton(new NavOrderApiClient(http));
        return services;
    }

    /// <summary>
    /// Fake nav-order source returning the built-in effective order for <c>GET /api/nav-order</c>.
    /// </summary>
    private sealed class StubNavOrderHandler : HttpMessageHandler
    {
        private const string Json = """
            [
              { "key": "home", "order": 5 },
              { "key": "activity", "order": 10 },
              { "key": "tools", "order": 20 },
              { "key": "chat", "order": 30 },
              { "key": "configuration", "order": 40 },
              { "key": "skills", "order": 50 },
              { "key": "agents", "order": 60 },
              { "key": "cron", "order": 70 }
            ]
            """;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(Json, System.Text.Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }
    }
}
