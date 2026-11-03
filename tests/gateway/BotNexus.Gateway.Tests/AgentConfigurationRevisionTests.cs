using System.IO.Abstractions;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Configuration.Store;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Moq;
using Xunit;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Tests-first contract for optional revision checks on authored agent edits. These tests use
/// existing controller signatures; runtime RED must be captured remotely before implementation.
/// </summary>
public sealed class AgentConfigurationRevisionTests
{
    private const string RevisionHeader = "X-BotNexus-Config-Revision";
    private const string InitialDocument = """
        {"agents":{"SaMpLe":{"provider":"test","model":"test","toolTimeoutSeconds":null,
        "fileAccess":{"allowedReadPaths":["existing-read"],"allowedWritePaths":["existing-write"],"deniedPaths":["private"],"future":"keep"},
        "toolPolicy":{"denied":["delete"],"askFallback":"deny","future":"keep"},
        "sessionAccess":{"level":"own","future":"keep"},"metadata":{"token":"private-agent-secret"}}},
        "gateway":{"fileAccess":{"allowedReadPaths":["world-read"],"allowedWritePaths":["world-write"],"deniedPaths":["world-private"]}},
        "opaque":{"secret":"private-root-secret","explicitNull":null,"empty":[]}}
        """;

    /// <summary>GET preserves the authored, redacted body and exposes the canonical whole-root revision.</summary>
    [Theory]
    [InlineData("absent")]
    [InlineData("corrupt")]
    [InlineData("conflicting")]
    public async Task GetConfiguration_CanonicalSnapshot_ReturnsRevisionAndNoStoreWithoutChangingBody(string projection)
    {
        await WithAuthority(projection, async (writer, path) =>
        {
            var before = await writer.ReadWithRevisionAsync();
            var controller = Controller();
            var result = (await controller.GetConfiguration("SAMPLE", writer, CancellationToken.None))
                .ShouldBeOfType<ContentResult>();
            var body = Body(result);
            var expected = before.Config.DeepClone().AsObject().ReadEditableConfiguration(AgentId.From("SaMpLe"));
            JsonNode.DeepEquals(body, expected).ShouldBeTrue();
            body.ContainsKey("toolTimeoutSeconds").ShouldBeTrue();
            body["toolTimeoutSeconds"].ShouldBeNull();
            body.ContainsKey("displayName").ShouldBeFalse();
            body.ContainsKey("metadata").ShouldBeFalse();
            body["fileAccess"]?["future"].ShouldBeNull();
            (result.Content ?? "").ShouldNotContain("private-agent-secret");
            controller.Response.Headers[RevisionHeader].Count.ShouldBe(1);
            controller.Response.Headers[RevisionHeader].ToString().ShouldBe(before.Revision);
            controller.Response.Headers.CacheControl.ToString().ShouldBe("no-store");
            JsonNode.DeepEquals(before.Config, await Fresh(path).ReadAsync()).ShouldBeTrue();
        });
    }

    /// <summary>A secret-only change outside this agent invalidates the whole-document token.</summary>
    [Fact]
    public async Task GetConfiguration_UnrelatedSecretChange_ChangesRevisionButNotAuthoredBody()
    {
        await WithAuthority("conflicting", async (writer, path) =>
        {
            var first = Controller();
            var firstBody = Body((await first.GetConfiguration("sample", writer, CancellationToken.None)).ShouldBeOfType<ContentResult>());
            var firstRevision = first.Response.Headers[RevisionHeader].ToString();
            firstRevision.ShouldNotBeNullOrWhiteSpace();
            await Fresh(path).MutateAsync(root => root["opaque"].ShouldBeOfType<JsonObject>()["secret"] = "rotated-private-secret", "test-secret-rotation");
            var second = Controller();
            var secondBody = Body((await second.GetConfiguration("sample", Fresh(path), CancellationToken.None)).ShouldBeOfType<ContentResult>());
            JsonNode.DeepEquals(firstBody, secondBody).ShouldBeTrue();
            second.Response.Headers[RevisionHeader].ToString().ShouldBe((await writer.ReadWithRevisionAsync()).Revision);
            second.Response.Headers[RevisionHeader].ToString().ShouldNotBe(firstRevision);
        });
    }

    /// <summary>Clients that omit the new header keep the established partial-edit behavior.</summary>
    [Fact]
    public async Task PatchConfiguration_MissingRevision_PreservesLegacyEditsAndPolicyDenials()
    {
        await WithAuthority("absent", async (writer, path) =>
        {
            var controller = Controller();
            (await controller.PatchConfiguration("SAMPLE", Patch("""{"fileAccess":{"allowedReadPaths":["existing-read","mailbox"]}}"""), writer, CancellationToken.None))
                .ShouldBeOfType<OkObjectResult>();
            await AssertPreservedPolicyAsync(path);
        });
    }

    /// <summary>The token returned by GET can guard a policy edit without losing authored siblings.</summary>
    [Fact]
    public async Task PatchConfiguration_CurrentGetRevision_AppliesAndPreservesPolicyDenials()
    {
        await WithAuthority("conflicting", async (writer, path) =>
        {
            var reader = Controller();
            (await reader.GetConfiguration("sample", writer, CancellationToken.None)).ShouldBeOfType<ContentResult>();
            var revision = reader.Response.Headers[RevisionHeader].ToString();
            revision.ShouldNotBeNullOrWhiteSpace();
            var controller = Controller(new StringValues(revision));
            (await controller.PatchConfiguration("SAMPLE", Patch("""{"fileAccess":{"allowedReadPaths":["existing-read","mailbox"]}}"""), Fresh(path), CancellationToken.None))
                .ShouldBeOfType<OkObjectResult>();
            await AssertPreservedPolicyAsync(path);
            (await writer.ReadWithRevisionAsync()).Revision.ShouldNotBe(revision);
        });
    }

    /// <summary>A present blank header is invalid, not the legacy missing-header mode.</summary>
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public async Task PatchConfiguration_BlankRevision_Returns400WithoutMutation(string revision)
    {
        await WithAuthority("absent", async (writer, path) =>
        {
            var before = await writer.ReadWithRevisionAsync();
            var controller = Controller(new StringValues(revision));
            controller.Request.Headers.ContainsKey(RevisionHeader).ShouldBeTrue();
            (await controller.PatchConfiguration("sample", Patch("""{"description":"must not save"}"""), writer, CancellationToken.None))
                .ShouldBeOfType<BadRequestObjectResult>();
            await AssertUnchangedAsync(path, before.Config, before.Revision);
        });
    }

    /// <summary>Header cardinality is checked even when two values happen to be identical.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PatchConfiguration_MultipleRevisionValues_Returns400WithoutMutation(bool secondBlank)
    {
        await WithAuthority("corrupt", async (writer, path) =>
        {
            var before = await writer.ReadWithRevisionAsync();
            var controller = Controller(new StringValues([before.Revision, secondBlank ? "" : before.Revision]));
            (await controller.PatchConfiguration("sample", Patch("""{"description":"must not save"}"""), writer, CancellationToken.None))
                .ShouldBeOfType<BadRequestObjectResult>();
            await AssertUnchangedAsync(path, before.Config, before.Revision);
        });
    }

    /// <summary>Stale snapshots conflict before editing or validation, including an empty patch.</summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"description\":\"must not save\"}")]
    [InlineData("{\"toolTimeoutSeconds\":0}")]
    public async Task PatchConfiguration_StaleRevision_ReturnsSafe409AndPreservesInterveningWrite(string json)
    {
        await WithAuthority("conflicting", async (writer, path) =>
        {
            var stale = await writer.ReadWithRevisionAsync();
            await Fresh(path).MutateAsync(root => root["opaque"].ShouldBeOfType<JsonObject>()["secret"] = "rotated-private-secret", "intervening-save");
            var before = await writer.ReadWithRevisionAsync();
            before.Revision.ShouldNotBe(stale.Revision);
            var controller = Controller(new StringValues(stale.Revision));
            var result = (await controller.PatchConfiguration("sample", Patch(json), writer, CancellationToken.None))
                .ShouldBeOfType<ConflictObjectResult>();
            var error = JsonSerializer.SerializeToNode(result.Value)?["error"]?.GetValue<string>();
            error.ShouldNotBeNullOrWhiteSpace();
            error.ShouldNotContain(path);
            error.ShouldNotContain(Path.GetDirectoryName(path) ?? throw new InvalidOperationException());
            error.ShouldNotContain(Path.GetFileName(path));
            error.ShouldNotContain(stale.Revision);
            error.ShouldNotContain(before.Revision);
            var response = JsonSerializer.Serialize(result.Value);
            response.ShouldNotContain(path);
            response.ShouldNotContain(Path.GetDirectoryName(path) ?? throw new InvalidOperationException());
            response.ShouldNotContain(stale.Revision);
            response.ShouldNotContain(before.Revision);
            response.ShouldNotContain("private");
            response.ShouldNotContain("Exception");
            await AssertUnchangedAsync(path, before.Config, before.Revision);
        });
    }

    /// <summary>A nonblank token remains opaque: an unrecognized value is a conflict, not a format error.</summary>
    [Theory]
    [InlineData("opaque-token-from-another-process")]
    [InlineData(" padded-token ")]
    public async Task PatchConfiguration_OpaqueRevision_Returns409WithoutMutation(string revision)
    {
        await WithAuthority("absent", async (writer, path) =>
        {
            var before = await writer.ReadWithRevisionAsync();
            (await Controller(new StringValues(revision)).PatchConfiguration("sample", Patch("{}"), writer, CancellationToken.None))
                .ShouldBeOfType<ConflictObjectResult>();
            await AssertUnchangedAsync(path, before.Config, before.Revision);
        });
    }

    /// <summary>Current tokens must not bypass existing validation or create missing/default agents.</summary>
    [Theory]
    [InlineData("sample", "{\"toolTimeoutSeconds\":0}", 400)]
    [InlineData("missing", "{}", 404)]
    [InlineData("defaults", "{}", 404)]
    public async Task PatchConfiguration_CurrentRevision_RetainsValidationAndNoResurrection(string id, string json, int status)
    {
        await WithAuthority("absent", async (writer, path) =>
        {
            var before = await writer.ReadWithRevisionAsync();
            var result = await Controller(new StringValues(before.Revision)).PatchConfiguration(id, Patch(json), writer, CancellationToken.None);
            if (status == 400) result.ShouldBeOfType<BadRequestObjectResult>();
            else result.ShouldBeOfType<NotFoundResult>();
            await AssertUnchangedAsync(path, before.Config, before.Revision);
        });
    }

    /// <summary>
    /// Existing file-policy semantics are whole-policy fallback, not field merging. A future
    /// mailbox installer must fail closed instead of materializing a partial inherited policy.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PatchConfiguration_AbsentOrNullFilePolicy_MaterializationDropsWholeWorldInheritance(bool explicitNull)
    {
        await WithAuthority("absent", async (writer, path) =>
        {
            await writer.MutateAsync(root =>
            {
                var agent = root["agents"]?["SaMpLe"].ShouldBeOfType<JsonObject>() ?? throw new InvalidOperationException();
                if (explicitNull) agent["fileAccess"] = null;
                else agent.Remove("fileAccess");
            }, "inherit-world-policy");
            var authored = (await writer.ReadAsync())["agents"]?["SaMpLe"].ShouldBeOfType<JsonObject>() ?? throw new InvalidOperationException();
            authored.ContainsKey("fileAccess").ShouldBe(explicitNull);
            var inherited = (await DescriptorAsync(writer, path)).FileAccess.ShouldNotBeNull();
            inherited.AllowedReadPaths.ShouldBe(new[] { "world-read" });
            inherited.AllowedWritePaths.ShouldBe(new[] { "world-write" });
            inherited.DeniedPaths.ShouldBe(new[] { "world-private" });
            var revision = (await writer.ReadWithRevisionAsync()).Revision;
            (await Controller(new StringValues(revision)).PatchConfiguration("sample", Patch("""{"fileAccess":{"allowedReadPaths":["mailbox"]}}"""), writer, CancellationToken.None))
                .ShouldBeOfType<OkObjectResult>();
            var own = (await DescriptorAsync(Fresh(path), path)).FileAccess.ShouldNotBeNull();
            own.AllowedReadPaths.ShouldBe(new[] { "mailbox" });
            own.AllowedWritePaths.ShouldBeEmpty();
            own.DeniedPaths.ShouldBeEmpty();
            var stored = (await Fresh(path).ReadAsync())["agents"]?["SaMpLe"]?["fileAccess"].ShouldBeOfType<JsonObject>() ?? throw new InvalidOperationException();
            stored.ContainsKey("allowedWritePaths").ShouldBeFalse();
            stored.ContainsKey("deniedPaths").ShouldBeFalse();
            (await Controller().PatchConfiguration("sample", Patch("""{"inherit":["fileAccess"]}"""), writer, CancellationToken.None))
                .ShouldBeOfType<OkObjectResult>();
            var restored = (await DescriptorAsync(Fresh(path), path)).FileAccess.ShouldNotBeNull();
            restored.DeniedPaths.ShouldBe(new[] { "world-private" });
            restored.AllowedWritePaths.ShouldBe(new[] { "world-write" });
        });
    }

    private static AgentsController Controller(StringValues? revision = null)
    {
        var controller = new AgentsController(
            Mock.Of<BotNexus.Gateway.Abstractions.Agents.IAgentRegistry>(),
            Mock.Of<BotNexus.Gateway.Abstractions.Agents.IAgentSupervisor>(),
            Mock.Of<BotNexus.Gateway.Abstractions.Agents.IAgentConfigurationWriter>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        if (revision.HasValue) controller.Request.Headers[RevisionHeader] = revision.Value;
        return controller;
    }

    private static AgentConfigurationPatch Patch(string json) =>
        JsonSerializer.Deserialize<AgentConfigurationPatch>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))
        ?? throw new InvalidOperationException("Patch must be an object.");

    private static JsonObject Body(ContentResult result) =>
        JsonNode.Parse(result.Content ?? "").ShouldBeOfType<JsonObject>();

    private static PlatformConfigWriter Fresh(string path) => new(path, new FileSystem());

    private static async Task AssertUnchangedAsync(string path, JsonObject before, string revision)
    {
        var after = await Fresh(path).ReadWithRevisionAsync();
        JsonNode.DeepEquals(before, after.Config).ShouldBeTrue();
        after.Revision.ShouldBe(revision);
    }

    private static async Task AssertPreservedPolicyAsync(string path)
    {
        var root = await Fresh(path).ReadAsync();
        var agent = root["agents"]?["SaMpLe"].ShouldBeOfType<JsonObject>() ?? throw new InvalidOperationException();
        root["agents"].ShouldBeOfType<JsonObject>().Select(entry => entry.Key).ShouldBe(new[] { "SaMpLe" });
        agent["fileAccess"]?["allowedReadPaths"].ShouldBeOfType<JsonArray>().Select(node => node?.GetValue<string>())
            .ShouldBe(new[] { "existing-read", "mailbox" });
        agent["fileAccess"]?["allowedWritePaths"]?[0]?.GetValue<string>().ShouldBe("existing-write");
        agent["fileAccess"]?["deniedPaths"]?[0]?.GetValue<string>().ShouldBe("private");
        agent["fileAccess"]?["future"]?.GetValue<string>().ShouldBe("keep");
        agent["toolPolicy"]?["denied"]?[0]?.GetValue<string>().ShouldBe("delete");
        agent["toolPolicy"]?["askFallback"]?.GetValue<string>().ShouldBe("deny");
        agent["toolPolicy"]?["future"]?.GetValue<string>().ShouldBe("keep");
        agent["sessionAccess"]?["future"]?.GetValue<string>().ShouldBe("keep");
        agent["metadata"]?["token"]?.GetValue<string>().ShouldBe("private-agent-secret");
        JsonNode.DeepEquals(root["opaque"], JsonNode.Parse(InitialDocument)?["opaque"]).ShouldBeTrue();
        var descriptor = await DescriptorAsync(Fresh(path), path);
        descriptor.FileAccess.ShouldNotBeNull().DeniedPaths.ShouldBe(new[] { "private" });
        var monitor = new Mock<IOptionsMonitor<PlatformConfig>>();
        monitor.Setup(m => m.CurrentValue).Returns(await Fresh(path).ReadPlatformConfigAsync());
        var policy = new BotNexus.Gateway.Security.DefaultToolPolicyProvider(monitor.Object,
            NullLogger<BotNexus.Gateway.Security.DefaultToolPolicyProvider>.Instance);
        policy.IsToolAvailable("delete", AgentId.From("SaMpLe")).ShouldBeFalse();
    }

    private static async Task<BotNexus.Gateway.Abstractions.Models.AgentDescriptor> DescriptorAsync(PlatformConfigWriter writer, string path)
    {
        var monitor = new Mock<IOptionsMonitor<PlatformConfig>>();
        monitor.Setup(m => m.CurrentValue).Returns(await writer.ReadPlatformConfigAsync());
        var source = new PlatformConfigAgentSource(monitor.Object, Path.GetDirectoryName(path) ?? throw new InvalidOperationException(),
            NullLogger<PlatformConfigAgentSource>.Instance);
        return (await source.LoadAsync()).ShouldHaveSingleItem();
    }

    private static async Task WithAuthority(string projection, Func<PlatformConfigWriter, string, Task> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "bn-agent-revision-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "config.json");
        try
        {
            var storePath = ConfigStoreBootstrap.ResolveStorePath(path, new FileSystem());
            var store = new SqliteConfigStore(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = storePath,
                Pooling = false
            }.ToString());
            await store.WriteDocumentAsync(JsonNode.Parse(InitialDocument).ShouldBeOfType<JsonObject>());
            if (projection != "absent")
                await File.WriteAllTextAsync(path, projection == "corrupt" ? "not-json" : """{"agents":{"SaMpLe":{"model":"wrong"}}}""");
            var writer = Fresh(path);
            var backend = writer.Backends.ShouldHaveSingleItem();
            backend.Name.ShouldBe("sqlite");
            backend.Location.ShouldBe(storePath);
            backend.WinsOnRead.ShouldBeTrue();
            JsonNode.DeepEquals(JsonNode.Parse(InitialDocument), await writer.ReadAsync()).ShouldBeTrue();
            await action(writer, path);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
