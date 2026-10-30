using System.IO.Abstractions;
using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration.Store;
using BotNexus.Gateway.Configuration.Writers;

namespace BotNexus.Gateway.Configuration.Tests;

/// <summary>
/// Characterizes why ordinary canonical mutations cannot implement explicit reconciliation.
/// These assertions preserve the ordinary-edit delta contract; a reconciliation extension must
/// not make these tests converge the documents implicitly.
/// </summary>
public sealed class ConfigReconciliationSeamTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), $"botnexus-reconcile-seam-{Guid.NewGuid():N}");
    private string ConfigPath => Path.Combine(_home, "config.json");
    private string StorePath => Path.Combine(_home, ConfigStoreBootstrap.StoreFileName);
    private readonly FileSystem _fileSystem = new();

    public ConfigReconciliationSeamTests() => Directory.CreateDirectory(_home);

    private async Task<PlatformConfigWriter> SeedAsync()
    {
        await ConfigStoreBootstrap.PopulateAsync(StorePath, JsonNode.Parse("""
            {"version":1,"gateway":{"listenUrl":"http://localhost:5000"},"extensionOwned":{"shared":"sql","sqlOnly":null}}
            """).ShouldBeOfType<JsonObject>());
        await File.WriteAllTextAsync(ConfigPath, """
            {"version":1,"gateway":{"listenUrl":"http://localhost:5000"},"extensionOwned":{"shared":"compatibility","fileOnly":null}}
            """);
        var writer = ConfigWriterFactory.Create(ConfigPath, _fileSystem);
        writer.Backends.ShouldContain(backend => backend.Name == "sqlite" && backend.Location == StorePath && backend.WinsOnRead);
        writer.Backends.ShouldContain(backend => backend.Name == "json" && backend.Location == ConfigPath);
        return writer;
    }

    [Fact]
    public async Task MutateDocumentValidatedAsync_NoOp_DoesNotReconcileUnrelatedDrift()
    {
        var writer = await SeedAsync();
        var before = File.ReadAllBytes(ConfigPath);
        var observed = "";
        var errors = await writer.MutateDocumentValidatedAsync(document =>
        {
            observed = document.ToJsonString();
            return null;
        }, "no-op-is-not-reconciliation");
        errors.ShouldBeEmpty();
        JsonNode.Parse(observed).ShouldBeOfType<JsonObject>()["extensionOwned"]
            .ShouldBeOfType<JsonObject>()["shared"].ShouldBeAssignableTo<JsonValue>().ShouldNotBeNull().GetValue<string>().ShouldBe("sql");
        File.ReadAllBytes(ConfigPath).ShouldBe(before);
        var report = await ConfigStoreParityDiagnostic.InspectAsync(ConfigPath);
        report.Status.ShouldBe(ConfigStoreParityStatus.Drift);
        report.SqlOnlyCount.ShouldBe(1);
        report.CompatibilityOnlyCount.ShouldBe(1);
        report.ChangedCount.ShouldBe(1);
    }

    [Fact]
    public async Task MutateValidatedAsync_ImportCompatibilityOnlyKey_StillDoesNotConvergeUnrelatedSharedConflict()
    {
        var writer = await SeedAsync();
        var errors = await writer.MutateValidatedAsync(document =>
        {
            // Deliberately model the tempting partial implementation. Importing the missing key
            // is not enough: SQL-only and shared-conflict properties never enter the delta.
            document["extensionOwned"].ShouldBeOfType<JsonObject>()["fileOnly"] = null;
            return null;
        }, "partial-union-is-not-reconciliation");
        errors.ShouldBeEmpty();
        var entries = await new SqliteConfigStore(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = StorePath
        }.ToString()).ReadEntriesAsync();
        entries["extensionOwned.fileOnly"].State.ShouldBe(ConfigValueState.ExplicitNull);
        entries["extensionOwned.sqlOnly"].State.ShouldBe(ConfigValueState.ExplicitNull);
        entries["extensionOwned.shared"].Value.ShouldBe("\"sql\"");
        var compatibility = JsonNode.Parse(await File.ReadAllTextAsync(ConfigPath)).ShouldBeOfType<JsonObject>();
        compatibility["extensionOwned"].ShouldBeOfType<JsonObject>().ContainsKey("sqlOnly").ShouldBeFalse();
        compatibility["extensionOwned"].ShouldBeOfType<JsonObject>()["shared"]
            .ShouldBeAssignableTo<JsonValue>().ShouldNotBeNull().GetValue<string>().ShouldBe("compatibility");
        (await ConfigStoreParityDiagnostic.InspectAsync(ConfigPath)).Status.ShouldBe(ConfigStoreParityStatus.Drift);
    }

    public void Dispose()
    {
        ConfigStoreBootstrap.ReleaseConnections(StorePath);
        Directory.Delete(_home, recursive: true);
    }
}
