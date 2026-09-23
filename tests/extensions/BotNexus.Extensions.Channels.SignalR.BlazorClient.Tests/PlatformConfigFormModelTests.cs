using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class PlatformConfigFormModelTests
{
    [Fact]
    public void Non_persisted_sections_are_owned_once_and_pinned()
    {
        PlatformConfigFormModel.NonPersistedSections.Order(StringComparer.OrdinalIgnoreCase)
            .ShouldBe(new[] { "$schema", "agents", "extensionRepositories", "version" });
    }

    [Fact]
    public async Task Config_change_marks_form_dirty_before_the_path_callback_arrives()
    {
        var model = new PlatformConfigFormModel(
            new PlatformConfigService(new HttpClient(new RecordingHandler()) { BaseAddress = new Uri("http://gateway.test") }));

        await model.LoadAsync();
        model.MarkConfigChanged(new JsonObject { ["gateway"] = new JsonObject() });

        model.IsDirty.ShouldBeTrue();
    }

    [Fact]
    public async Task Save_quotes_loaded_revision_and_advances_revision_after_success()
    {
        var handler = new RecordingHandler();
        var model = new PlatformConfigFormModel(
            new PlatformConfigService(new HttpClient(handler) { BaseAddress = new Uri("http://gateway.test") }));

        await model.LoadAsync();
        model.MarkPathChanged("gateway.listenUrl");
        model.MarkConfigChanged(new JsonObject
        {
            ["gateway"] = new JsonObject { ["listenUrl"] = "http://localhost:9999" }
        });

        var outcome = await model.SaveAsync();

        outcome.Success.ShouldBeTrue();
        handler.ExpectedRevisions.ShouldBe(new[] { "REV-1" });
        model.Revision.ShouldBe("REV-2");
        model.IsDirty.ShouldBeFalse();
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private string _revision = "REV-1";
        public List<string?> ExpectedRevisions { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath;
            if (path == "/api/config/schema")
                return Json(new JsonObject { ["schema"] = new JsonObject { ["properties"] = new JsonObject() } });
            if (path == "/api/config")
            {
                if (request.Method == HttpMethod.Get)
                    return Json(new JsonObject { ["gateway"] = new JsonObject { ["listenUrl"] = "http://localhost:5000" } });
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
                ExpectedRevisions.Add(body["expectedRevision"]?.GetValue<string>());
                _revision = "REV-2";
                return Json(new JsonObject { ["success"] = true, ["revision"] = _revision, ["errors"] = new JsonArray() });
            }
            if (path == "/api/config/snapshot")
                return Json(new JsonObject { ["revision"] = _revision, ["config"] = new JsonObject() });

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(JsonObject value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(value.ToJsonString(), Encoding.UTF8, "application/json")
        };
    }
}