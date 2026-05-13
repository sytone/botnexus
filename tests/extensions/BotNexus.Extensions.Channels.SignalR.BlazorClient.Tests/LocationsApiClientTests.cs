using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Unit tests for <see cref="LocationsApiClient"/>.
/// Validates HTTP call patterns and error handling for the locations REST API.
/// </summary>
public sealed class LocationsApiClientTests
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // ── List ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListAsync_returns_locations_on_success()
    {
        var expected = new List<LocationDto>
        {
            new() { Name = "loc1", Type = "filesystem", PathOrEndpoint = "/path", Status = "healthy", IsUserDefined = true }
        };

        using var handler = new MockHandler(HttpStatusCode.OK, JsonSerializer.Serialize(expected, JsonOpts));
        var client = new LocationsApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://test") });

        var (locations, error) = await client.ListAsync();

        error.ShouldBeNull();
        locations.Count.ShouldBe(1);
        locations[0].Name.ShouldBe("loc1");
    }

    [Fact]
    public async Task ListAsync_returns_error_on_failure()
    {
        using var handler = new MockHandler(HttpStatusCode.InternalServerError, """{"error":"fail"}""");
        var client = new LocationsApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://test") });

        var (locations, error) = await client.ListAsync();

        locations.Count.ShouldBe(0);
        error.ShouldNotBeNull();
    }

    // ── Create ──────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_returns_location_on_success()
    {
        var response = new LocationDto { Name = "new-loc", Type = "filesystem", Status = "unknown", IsUserDefined = true };
        using var handler = new MockHandler(HttpStatusCode.Created, JsonSerializer.Serialize(response, JsonOpts));
        var client = new LocationsApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://test") });

        var (location, error) = await client.CreateAsync(new UpsertLocationDto
        {
            Name = "new-loc",
            Type = "filesystem",
            Value = "/path"
        });

        error.ShouldBeNull();
        location.ShouldNotBeNull();
        location.Name.ShouldBe("new-loc");
    }

    [Fact]
    public async Task CreateAsync_returns_error_on_conflict()
    {
        using var handler = new MockHandler(HttpStatusCode.Conflict, """{"error":"Location 'dup' already exists."}""");
        var client = new LocationsApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://test") });

        var (location, error) = await client.CreateAsync(new UpsertLocationDto { Name = "dup" });

        location.ShouldBeNull();
        error.ShouldContain("already exists");
    }

    // ── Update ──────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateAsync_returns_location_on_success()
    {
        var response = new LocationDto { Name = "upd", Type = "api", Status = "unknown", IsUserDefined = true };
        using var handler = new MockHandler(HttpStatusCode.OK, JsonSerializer.Serialize(response, JsonOpts));
        var client = new LocationsApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://test") });

        var (location, error) = await client.UpdateAsync("upd", new UpsertLocationDto
        {
            Name = "upd",
            Type = "api",
            Value = "https://example.com"
        });

        error.ShouldBeNull();
        location.ShouldNotBeNull();
    }

    [Fact]
    public async Task UpdateAsync_returns_error_on_not_found()
    {
        using var handler = new MockHandler(HttpStatusCode.NotFound, """{"error":"Not found."}""");
        var client = new LocationsApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://test") });

        var (location, error) = await client.UpdateAsync("missing", new UpsertLocationDto());

        location.ShouldBeNull();
        error.ShouldNotBeNull();
    }

    // ── Delete ──────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteAsync_returns_success_on_204()
    {
        using var handler = new MockHandler(HttpStatusCode.NoContent, "");
        var client = new LocationsApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://test") });

        var (success, error) = await client.DeleteAsync("loc");

        success.ShouldBeTrue();
        error.ShouldBeNull();
    }

    [Fact]
    public async Task DeleteAsync_returns_error_on_not_found()
    {
        using var handler = new MockHandler(HttpStatusCode.NotFound, """{"error":"Not found."}""");
        var client = new LocationsApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://test") });

        var (success, error) = await client.DeleteAsync("missing");

        success.ShouldBeFalse();
        error.ShouldNotBeNull();
    }

    // ── CheckHealth ─────────────────────────────────────────────────────

    [Fact]
    public async Task CheckHealthAsync_returns_result_on_success()
    {
        var response = new LocationHealthDto { Name = "loc", Status = "healthy", Message = "OK" };
        using var handler = new MockHandler(HttpStatusCode.OK, JsonSerializer.Serialize(response, JsonOpts));
        var client = new LocationsApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://test") });

        var (result, error) = await client.CheckHealthAsync("loc");

        error.ShouldBeNull();
        result.ShouldNotBeNull();
        result.Status.ShouldBe("healthy");
    }

    [Fact]
    public async Task CheckHealthAsync_returns_error_on_not_found()
    {
        using var handler = new MockHandler(HttpStatusCode.NotFound, """{"error":"Not found."}""");
        var client = new LocationsApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://test") });

        var (result, error) = await client.CheckHealthAsync("missing");

        result.ShouldBeNull();
        error.ShouldNotBeNull();
    }

    // ── Exception handling ──────────────────────────────────────────────

    [Fact]
    public async Task ListAsync_returns_error_on_network_exception()
    {
        using var handler = new ThrowingHandler();
        var client = new LocationsApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://test") });

        var (locations, error) = await client.ListAsync();

        locations.Count.ShouldBe(0);
        error.ShouldNotBeNull();
    }

    // ── Test helpers ────────────────────────────────────────────────────

    private sealed class MockHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _responseBody;

        public MockHandler(HttpStatusCode statusCode, string responseBody)
        {
            _statusCode = statusCode;
            _responseBody = responseBody;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_responseBody, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw new HttpRequestException("Network error");
        }
    }
}
