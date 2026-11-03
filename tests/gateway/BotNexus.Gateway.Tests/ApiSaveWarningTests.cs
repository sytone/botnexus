using System.IO.Abstractions;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Extensions;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Configuration.Store;
using BotNexus.Gateway.Configuration.Writers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace BotNexus.Gateway.Tests;

/// <summary>Exercises request-owned save diagnostics through controllers and real SQLite authority.</summary>
public sealed class ApiSaveWarningTests : IDisposable
{
    private const string RefreshCode = "configuration-projection-refresh-failed";
    private const string RefreshMessage = "Configuration committed to SQLite; the existing JSON output projection could not be refreshed.";
    private const string BackupCode = "configuration-backup-permissions-failed";
    private const string BackupMessage = "Configuration committed to SQLite; permissions on a JSON backup could not be restricted.";
    private const string RetainedHost = "127.0.0.1";
    private const string Secret = "private-config-secret";
    private const string ExceptionMarker = "private-exception-marker";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bn-api-warning-" + Guid.NewGuid().ToString("N"));
    private readonly ConfigController _config = new();
    private readonly FaultFileSystem _files = new();
    private readonly IExtensionLoader _extensions;
    private readonly AgentsController _agents = new(Mock.Of<IAgentRegistry>(), Mock.Of<IAgentSupervisor>(), Mock.Of<IAgentConfigurationWriter>())
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
    };
    private string Projection => Path.Combine(_directory, "config.json");
    private string Backups => Path.Combine(_directory, "backups");

    public ApiSaveWarningTests()
    {
        Directory.CreateDirectory(_directory);
        var extensions = new Mock<IExtensionLoader>();
        extensions.Setup(value => value.GetLoaded()).Returns([]);
        _extensions = extensions.Object;
    }

    [Theory]
    [InlineData("patch", "projection")]
    [InlineData("section", "projection")]
    [InlineData("entry", "projection")]
    [InlineData("delete", "projection")]
    [InlineData("agent", "projection")]
    [InlineData("patch", "backup")]
    [InlineData("section", "backup")]
    [InlineData("entry", "backup")]
    [InlineData("delete", "backup")]
    [InlineData("agent", "backup")]
    public async Task Save_CommittedWarningRemainsOnA_AfterBNoOp(string endpoint, string fault)
    {
        var (writer, store) = await CreateWriter("existing");
        var before = await store.ReadSnapshotAsync();
        var oldRevision = (await writer.ReadWithRevisionAsync()).Revision;
        _files.FailRead = fault == "projection";
        _files.FailBackupPermission = fault == "backup";

        // Hold the real response object, not serialized bytes, across a later request.
        var first = await Save(endpoint, writer);
        var second = await Save(endpoint, writer);

        first.StatusCode.ShouldBe(StatusCodes.Status200OK);
        second.StatusCode.ShouldBe(StatusCodes.Status200OK);
        AssertWarning(Body(first), fault == "backup" ? BackupCode : RefreshCode,
            fault == "backup" ? BackupMessage : RefreshMessage);
        Warnings(Body(second)).ShouldBeEmpty();
        var snapshot = await store.ReadSnapshotAsync();
        snapshot.Revision.ShouldBe(before.Revision + 1);
        var canonical = await writer.ReadWithRevisionAsync();
        canonical.Revision.ShouldNotBe(oldRevision);
        if (endpoint == "patch")
        {
            Body(first)["success"].ShouldNotBeNull().GetValue<bool>().ShouldBeTrue();
            Body(first)["revision"].ShouldNotBeNull().GetValue<string>().ShouldBe(canonical.Revision);
            Body(first)["errors"].ShouldBeOfType<JsonArray>().ShouldBeEmpty();
        }
        else if (endpoint == "agent")
            Body(first)["saved"].ShouldNotBeNull().GetValue<bool>().ShouldBeTrue();
        else
            Body(first)["message"].ShouldNotBeNull().GetValue<string>().ShouldNotBeNullOrWhiteSpace();
        if (endpoint == "agent")
            canonical.Config["agents"]?["sample"]?["description"].ShouldNotBeNull().GetValue<string>().ShouldBe("updated");
        else if (endpoint == "delete")
            canonical.Config["gateway"].ShouldBeOfType<JsonObject>().ContainsKey("port").ShouldBeFalse();
        else
            canonical.Config["gateway"]?["port"].ShouldNotBeNull().GetValue<int>().ShouldBe(6000);
        canonical.Config["providers"]?["test"]?["apiKey"].ShouldNotBeNull().GetValue<string>().ShouldBe(Secret);
        _files.ReadAttempts.ShouldBe(1);
        if (fault == "projection")
            (await File.ReadAllTextAsync(Projection)).ShouldBe("stale-output");
        else
        {
            _files.BackupPermissionAttempts.ShouldBe(1);
            Directory.GetFiles(Backups).Length.ShouldBe(1);
            JsonNode.DeepEquals(JsonNode.Parse(await File.ReadAllTextAsync(Projection)), canonical.Config).ShouldBeTrue();
        }
    }

    [Theory]
    [InlineData("patch")]
    [InlineData("section")]
    [InlineData("entry")]
    [InlineData("delete")]
    [InlineData("agent")]
    public async Task Save_MissingProjection_CommitsWithoutCreatingOutputOrWarnings(string endpoint)
    {
        var (writer, store) = await CreateWriter("absent");
        var before = await store.ReadSnapshotAsync();
        var result = await Save(endpoint, writer);
        Warnings(Body(result)).ShouldBeEmpty();
        (await store.ReadSnapshotAsync()).Revision.ShouldBe(before.Revision + 1);
        File.Exists(Projection).ShouldBeFalse();
        Directory.Exists(Backups).ShouldBeFalse();
        _files.ReadAttempts.ShouldBe(0);
    }

    [Theory]
    [InlineData("patch")]
    [InlineData("section")]
    [InlineData("entry")]
    [InlineData("delete")]
    [InlineData("agent")]
    public async Task Save_EmptyExistingProjection_RefreshesFromAuthorityWithoutWarnings(string endpoint)
    {
        var (writer, store) = await CreateWriter("existing");
        await File.WriteAllTextAsync(Projection, string.Empty);
        var before = await store.ReadSnapshotAsync();
        Warnings(Body(await Save(endpoint, writer))).ShouldBeEmpty();
        (await store.ReadSnapshotAsync()).Revision.ShouldBe(before.Revision + 1);
        JsonNode.DeepEquals(JsonNode.Parse(await File.ReadAllTextAsync(Projection)), await writer.ReadAsync()).ShouldBeTrue();
        (await File.ReadAllTextAsync(Directory.GetFiles(Backups).ShouldHaveSingleItem())).ShouldBeEmpty();
        _files.ReadAttempts.ShouldBe(1);
    }

    [Theory]
    [InlineData("patch")]
    [InlineData("section")]
    [InlineData("entry")]
    [InlineData("delete")]
    [InlineData("agent")]
    public async Task Save_PreCommitCancellationAfterA_DoesNotWriteOrReturnStaleOutcome(string endpoint)
    {
        var (writer, store) = await CreateWriter("existing");
        _files.FailRead = true;
        var first = await Save(endpoint, writer);
        var before = await store.ReadSnapshotAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => Save(endpoint, writer, cancellation.Token));
        (await store.ReadSnapshotAsync()).Revision.ShouldBe(before.Revision);
        _files.ReadAttempts.ShouldBe(1);
        AssertWarning(Body(first), RefreshCode, RefreshMessage);
    }

    [Theory]
    [InlineData("patch")]
    [InlineData("agent")]
    public async Task Patch_RejectionAndConflictAfterA_HaveNoWarningsOrPrivateDetails(string endpoint)
    {
        var (writer, store) = await CreateWriter("existing");
        var staleRevision = (await writer.ReadWithRevisionAsync()).Revision;
        _files.FailRead = true;
        var first = await Save(endpoint, writer);
        var before = await store.ReadSnapshotAsync();
        ObjectResult rejected;
        ObjectResult conflict;
        if (endpoint == "patch")
        {
            rejected = (await _config.PatchConfig(new ConfigPatchRequest([]), writer, _extensions, default)).Result.ShouldBeOfType<BadRequestObjectResult>();
            conflict = (await _config.PatchConfig(new ConfigPatchRequest([new("gateway.port", JsonValue.Create(7000))], staleRevision), writer, _extensions, default)).Result.ShouldBeOfType<ConflictObjectResult>();
            Body(conflict)["revision"].ShouldNotBeNull().GetValue<string>().ShouldBe((await writer.ReadWithRevisionAsync()).Revision);
            Body(conflict)["errors"].ShouldBeOfType<JsonArray>().ShouldHaveSingleItem().ShouldNotBeNull().GetValue<string>()
                .ShouldBe("Configuration has changed. Reload it before saving again.");
        }
        else
        {
            rejected = (await _agents.PatchConfiguration("sample", AgentPatch("{\"toolTimeoutSeconds\":-1}"), writer, default)).ShouldBeOfType<BadRequestObjectResult>();
            _agents.Request.Headers["X-BotNexus-Config-Revision"] = staleRevision;
            conflict = (await _agents.PatchConfiguration("sample", AgentPatch("{\"description\":\"not committed\"}"), writer, default)).ShouldBeOfType<ConflictObjectResult>();
            Body(conflict)["error"].ShouldNotBeNull().GetValue<string>().ShouldBe("Configuration has changed. Reload it before saving again.");
        }
        rejected.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        foreach (var response in new[] { Body(rejected), Body(conflict) })
        {
            if (response["warnings"] is JsonArray warnings) warnings.ShouldBeEmpty();
            AssertSafe(response);
        }
        conflict.StatusCode.ShouldBe(StatusCodes.Status409Conflict);
        (await store.ReadSnapshotAsync()).Revision.ShouldBe(before.Revision);
        _files.ReadAttempts.ShouldBe(1);
        AssertWarning(Body(first), RefreshCode, RefreshMessage);
    }

    [Fact]
    public void ConfigPatchResponse_LegacyPayloadWithoutWarnings_DefaultsToEmptyAndPreservesDeconstruction()
    {
        var response = JsonSerializer.Deserialize<ConfigPatchResponse>("{\"success\":true,\"revision\":\"old\",\"errors\":[]}", new JsonSerializerOptions(JsonSerializerDefaults.Web)).ShouldNotBeNull();
        Warnings(JsonSerializer.SerializeToNode(response, new JsonSerializerOptions(JsonSerializerDefaults.Web)).ShouldBeOfType<JsonObject>()).ShouldBeEmpty();
        var (success, revision, errors) = response;
        success.ShouldBeTrue();
        revision.ShouldBe("old");
        errors.ShouldBeEmpty();
    }

    private async Task<(PlatformConfigWriter Writer, SqliteConfigStore Store)> CreateWriter(string projection)
    {
        var path = Path.Combine(_directory, ConfigStoreBootstrap.StoreFileName);
        var store = new SqliteConfigStore($"Data Source={path};Pooling=False");
        await store.WriteDocumentAsync(JsonNode.Parse("{\"gateway\":{\"port\":5000,\"host\":\"" + RetainedHost + "\"},\"providers\":{\"test\":{\"apiKey\":\"" + Secret + "\"}},\"agents\":{\"sample\":{\"provider\":\"test\",\"model\":\"test\"}}}").ShouldBeOfType<JsonObject>());
        if (projection != "absent") await File.WriteAllTextAsync(Projection, "stale-output");
        _files.Projection = Projection;
        _files.Backups = Backups;
        var backend = new AuthorityConfigurationWriter(store, path, Projection, _files, new ConfigBackupService(Backups, _files));
        var writer = new PlatformConfigWriter(Projection, _files, null, backend, store);
        var descriptor = writer.Backends.ShouldHaveSingleItem();
        descriptor.Name.ShouldBe("sqlite");
        descriptor.Location.ShouldBe(path);
        descriptor.WinsOnRead.ShouldBeTrue();
        return (writer, store);
    }

    private async Task<ObjectResult> Save(string endpoint, PlatformConfigWriter writer, CancellationToken ct = default)
    {
        var result = endpoint switch
        {
            "patch" => (await _config.PatchConfig(new ConfigPatchRequest([new("gateway.port", JsonValue.Create(6000))]), writer, _extensions, ct)).Result.ShouldBeOfType<OkObjectResult>(),
            "section" => (await _config.UpdateSection("gateway", new JsonObject { ["port"] = 6000, ["host"] = RetainedHost }, writer, _extensions, ct)).ShouldBeOfType<OkObjectResult>(),
            "entry" => (await _config.UpdateSectionEntry("gateway", "port", JsonValue.Create(6000), writer, _extensions, ct)).ShouldBeOfType<OkObjectResult>(),
            "delete" => (await _config.DeleteSectionEntry("gateway", "port", writer, _extensions, ct)).ShouldBeOfType<OkObjectResult>(),
            "agent" => (await _agents.PatchConfiguration("sample", AgentPatch("{\"description\":\"updated\"}"), writer, ct)).ShouldBeOfType<OkObjectResult>(),
            _ => throw new ArgumentOutOfRangeException(nameof(endpoint))
        };

        // Targeted DELETE must retain a real sibling, not bypass the destructive-section guard.
        // Verification must not supply the cancellation expected from the endpoint itself.
        var canonical = await writer.ReadAsync(CancellationToken.None);
        canonical["gateway"]?["host"].ShouldNotBeNull().GetValue<string>().ShouldBe(RetainedHost);
        return result;
    }

    private static AgentConfigurationPatch AgentPatch(string json) => JsonSerializer.Deserialize<AgentConfigurationPatch>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)).ShouldNotBeNull();
    private static JsonObject Body(ObjectResult result) => JsonSerializer.SerializeToNode(result.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web)).ShouldBeOfType<JsonObject>();
    private static JsonArray Warnings(JsonObject body) => body["warnings"].ShouldBeOfType<JsonArray>();

    private void AssertWarning(JsonObject body, string code, string message)
    {
        var warning = Warnings(body).ShouldHaveSingleItem().ShouldBeOfType<JsonObject>();
        warning.Select(item => item.Key).OrderBy(key => key).ShouldBe(new[] { "code", "message" });
        warning["code"].ShouldNotBeNull().GetValue<string>().ShouldBe(code);
        warning["message"].ShouldNotBeNull().GetValue<string>().ShouldBe(message);
        AssertSafe(warning);
    }

    private void AssertSafe(JsonObject body)
    {
        var json = body.ToJsonString();
        json.ShouldNotContain(_directory);
        json.ShouldNotContain(Projection);
        json.ShouldNotContain(Secret);
        json.ShouldNotContain(ExceptionMarker);
        json.ShouldNotContain("IOException");
        json.ShouldNotContain("UnauthorizedAccessException");
    }

    private sealed class FaultFileSystem : FileSystem
    {
        private readonly IFile _file;
        public FaultFileSystem() => _file = new FaultFile(this);
        public override IFile File => _file;
        public string Projection { get; set; } = string.Empty;
        public string Backups { get; set; } = string.Empty;
        public bool FailRead { get; set; }
        public bool FailBackupPermission { get; set; }
        public int ReadAttempts { get; private set; }
        public int BackupPermissionAttempts { get; private set; }

        private sealed class FaultFile(FaultFileSystem owner) : FileWrapper(owner)
        {
            public override Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default)
            {
                if (path == owner.Projection)
                {
                    owner.ReadAttempts++;
                    if (owner.FailRead) throw new IOException(ExceptionMarker + " " + path + " " + Secret);
                }
                return base.ReadAllTextAsync(path, cancellationToken);
            }

            public override bool Exists(string? path)
            {
                if (owner.FailBackupPermission && path is not null && owner.Path.GetDirectoryName(path) == owner.Backups)
                {
                    owner.BackupPermissionAttempts++;
                    throw new UnauthorizedAccessException(ExceptionMarker + " " + path + " " + Secret);
                }
                return base.Exists(path);
            }
        }
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
