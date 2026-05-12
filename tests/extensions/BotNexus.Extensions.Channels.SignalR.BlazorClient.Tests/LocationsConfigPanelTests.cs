using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components.Config;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class LocationsConfigPanelTests : IDisposable
{
    private readonly BunitContext _ctx = new();

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void Renders_empty_state_when_no_locations_exist()
    {
        var handler = new FakeLocationsApiHandler();
        ConfigureServices(handler);

        var cut = _ctx.Render<LocationsConfigPanel>();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.ShouldContain("No locations configured.");
        });
    }

    [Fact]
    public void Renders_populated_locations_and_hides_edit_for_system_locations()
    {
        var handler = new FakeLocationsApiHandler(
        [
            new LocationDto
            {
                Name = "workspace",
                Type = "filesystem",
                PathOrEndpoint = "Q:\\repos\\workspace",
                Description = "User location",
                Status = "healthy",
                IsUserDefined = true
            },
            new LocationDto
            {
                Name = "gateway-api",
                Type = "api",
                PathOrEndpoint = "https://gateway.test",
                Status = "unknown",
                IsUserDefined = false
            }
        ]);
        ConfigureServices(handler);

        var cut = _ctx.Render<LocationsConfigPanel>();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.ShouldContain("workspace");
            cut.Markup.ShouldContain("gateway-api");
            cut.Markup.ShouldContain("system");
        });

        cut.FindAll("button[title='Edit']").Count.ShouldBe(1);
        cut.FindAll("button[title='Delete']").Count.ShouldBe(1);
    }

    [Fact]
    public void Add_location_calls_post_api_and_refreshes_screen_state()
    {
        var handler = new FakeLocationsApiHandler();
        ConfigureServices(handler);
        var cut = _ctx.Render<LocationsConfigPanel>();

        cut.Find(".pcfg-add-btn").Click();
        cut.Find("input[placeholder='e.g. my-workspace']").Change("new-location");
        cut.Find("input[placeholder='/path/to/directory']").Change("Q:\\repos\\new-location");
        cut.Find("input[placeholder='Optional description']").Change("Added from UI");
        cut.Find(".pcfg-location-form .toolbar-btn.primary").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.ShouldContain("new-location");
            cut.Markup.ShouldContain("Q:\\repos\\new-location");
        });

        handler.Calls.Any(call => call.Method == HttpMethod.Post && call.Path == "/api/locations").ShouldBeTrue();
    }

    [Fact]
    public void Edit_location_calls_put_api_and_updates_screen_state()
    {
        var handler = new FakeLocationsApiHandler(
        [
            new LocationDto
            {
                Name = "workspace",
                Type = "filesystem",
                PathOrEndpoint = "Q:\\repos\\workspace",
                IsUserDefined = true
            }
        ]);
        ConfigureServices(handler);
        var cut = _ctx.Render<LocationsConfigPanel>();

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("workspace"));
        cut.Find("button[title='Edit']").Click();
        cut.Find("input[placeholder='/path/to/directory']").Change("Q:\\repos\\workspace-updated");
        cut.Find(".pcfg-location-form .toolbar-btn.primary").Click();

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("workspace-updated"));
        handler.Calls.Any(call => call.Method == HttpMethod.Put && call.Path == "/api/locations/workspace").ShouldBeTrue();
    }

    [Fact]
    public void Delete_location_calls_delete_api_and_removes_row()
    {
        var handler = new FakeLocationsApiHandler(
        [
            new LocationDto
            {
                Name = "to-delete",
                Type = "filesystem",
                PathOrEndpoint = "Q:\\repos\\to-delete",
                IsUserDefined = true
            }
        ]);
        ConfigureServices(handler);
        var cut = _ctx.Render<LocationsConfigPanel>();

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("to-delete"));
        cut.Find("button[title='Delete']").Click();

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("No locations configured."));
        handler.Calls.Any(call => call.Method == HttpMethod.Delete && call.Path == "/api/locations/to-delete").ShouldBeTrue();
    }

    [Fact]
    public void Validation_errors_block_api_call_for_blank_form_fields()
    {
        var handler = new FakeLocationsApiHandler();
        ConfigureServices(handler);
        var cut = _ctx.Render<LocationsConfigPanel>();

        cut.Find(".pcfg-add-btn").Click();
        cut.Find(".pcfg-location-form .toolbar-btn.primary").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.ShouldContain("Location name is required.");
            cut.Markup.ShouldContain("Path is required.");
        });

        handler.Calls.Count(call => call.Method == HttpMethod.Post && call.Path == "/api/locations").ShouldBe(0);
    }

    [Fact]
    public void Create_api_error_is_displayed_and_form_stays_open()
    {
        var handler = new FakeLocationsApiHandler
        {
            CreateError = "Location already exists."
        };
        ConfigureServices(handler);
        var cut = _ctx.Render<LocationsConfigPanel>();

        cut.Find(".pcfg-add-btn").Click();
        cut.Find("input[placeholder='e.g. my-workspace']").Change("duplicate");
        cut.Find("input[placeholder='/path/to/directory']").Change("Q:\\repos\\duplicate");
        cut.Find(".pcfg-location-form .toolbar-btn.primary").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find("[role='alert']").TextContent.ShouldContain("Location already exists.");
            cut.Markup.ShouldContain("New Location");
        });
    }

    [Fact]
    public void Shows_loading_then_data_when_initial_request_is_delayed()
    {
        var gate = new TaskCompletionSource<bool>();
        var handler = new FakeLocationsApiHandler(
            [
                new LocationDto
                {
                    Name = "delayed",
                    Type = "filesystem",
                    PathOrEndpoint = "Q:\\repos\\delayed",
                    IsUserDefined = true
                }
            ],
            gate.Task);
        ConfigureServices(handler);

        var cut = _ctx.Render<LocationsConfigPanel>();
        cut.Markup.ShouldContain("Loading locations…");

        gate.SetResult(true);

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("delayed"));
    }

    private void ConfigureServices(FakeLocationsApiHandler handler)
    {
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://gateway.test")
        };

        _ctx.Services.AddSingleton(httpClient);
        _ctx.Services.AddSingleton(new LocationsApiClient(httpClient));
    }

    private sealed class FakeLocationsApiHandler : HttpMessageHandler
    {
        private readonly List<LocationDto> _locations;
        private readonly Task _initialListGate;
        private bool _firstListRequest = true;

        public FakeLocationsApiHandler(IEnumerable<LocationDto>? initialLocations = null, Task? initialListGate = null)
        {
            _locations = initialLocations?.Select(Clone).ToList() ?? [];
            _initialListGate = initialListGate ?? Task.CompletedTask;
        }

        public string? CreateError { get; init; }

        public List<(HttpMethod Method, string Path, string Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add((request.Method, path, body));

            if (_firstListRequest && path == "/api/locations" && request.Method == HttpMethod.Get)
            {
                _firstListRequest = false;
                await _initialListGate;
            }

            if (path == "/api/locations" && request.Method == HttpMethod.Get)
                return JsonResponse(_locations);

            if (path == "/api/locations" && request.Method == HttpMethod.Post)
            {
                if (!string.IsNullOrWhiteSpace(CreateError))
                    return JsonError(HttpStatusCode.BadRequest, CreateError);

                var upsert = JsonSerializer.Deserialize<UpsertLocationDto>(body, SerializerOptions) ?? new UpsertLocationDto();
                var created = new LocationDto
                {
                    Name = upsert.Name,
                    Type = upsert.Type,
                    PathOrEndpoint = upsert.Value,
                    Description = upsert.Description,
                    Status = "unknown",
                    IsUserDefined = true
                };
                _locations.RemoveAll(loc => string.Equals(loc.Name, created.Name, StringComparison.OrdinalIgnoreCase));
                _locations.Add(created);
                return JsonResponse(created, HttpStatusCode.Created);
            }

            if (path.StartsWith("/api/locations/", StringComparison.Ordinal) && request.Method == HttpMethod.Put)
            {
                var name = Uri.UnescapeDataString(path["/api/locations/".Length..]);
                var upsert = JsonSerializer.Deserialize<UpsertLocationDto>(body, SerializerOptions) ?? new UpsertLocationDto();
                var index = _locations.FindIndex(loc => string.Equals(loc.Name, name, StringComparison.OrdinalIgnoreCase));
                if (index < 0)
                    return JsonError(HttpStatusCode.NotFound, "Location not found.");

                _locations[index] = new LocationDto
                {
                    Name = _locations[index].Name,
                    Type = upsert.Type,
                    PathOrEndpoint = upsert.Value,
                    Description = upsert.Description,
                    Status = _locations[index].Status,
                    IsUserDefined = true
                };
                return JsonResponse(_locations[index]);
            }

            if (path.StartsWith("/api/locations/", StringComparison.Ordinal) &&
                !path.EndsWith("/check", StringComparison.Ordinal) &&
                request.Method == HttpMethod.Delete)
            {
                var name = Uri.UnescapeDataString(path["/api/locations/".Length..]);
                _locations.RemoveAll(loc => string.Equals(loc.Name, name, StringComparison.OrdinalIgnoreCase));
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (path.EndsWith("/check", StringComparison.Ordinal) && request.Method == HttpMethod.Post)
            {
                var name = Uri.UnescapeDataString(path["/api/locations/".Length..^"/check".Length]);
                return JsonResponse(new LocationHealthDto
                {
                    Name = name,
                    Status = "healthy",
                    Message = "ok"
                });
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage JsonResponse<T>(T payload, HttpStatusCode statusCode = HttpStatusCode.OK)
            => new(statusCode)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload, SerializerOptions), Encoding.UTF8, "application/json")
            };

        private static HttpResponseMessage JsonError(HttpStatusCode statusCode, string error)
            => JsonResponse(new { error }, statusCode);

        private static LocationDto Clone(LocationDto dto) => new()
        {
            Name = dto.Name,
            Type = dto.Type,
            PathOrEndpoint = dto.PathOrEndpoint,
            Description = dto.Description,
            Status = dto.Status,
            IsUserDefined = dto.IsUserDefined
        };

        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };
    }
}
