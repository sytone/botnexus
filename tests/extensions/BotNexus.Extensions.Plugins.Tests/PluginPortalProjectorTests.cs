using System.Text.Json;
using BotNexus.Extensions.Plugins.Lifecycle;
using BotNexus.Extensions.Plugins.Portal;

namespace BotNexus.Extensions.Plugins.Tests;

/// <summary>
/// Tests the portal projection of installed plugins (#2687): the derived trust state, the
/// deliberately-uncollapsed update state, and the ordering the portal row set depends on.
/// </summary>
public sealed class PluginPortalProjectorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "botnexus-plugin-portal-tests",
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void List_orders_rows_by_name_so_the_rendered_order_is_stable()
    {
        var store = new PluginStateStore(_root);
        // Written out of order deliberately: the state file's write order must not leak into the
        // rendered row order.
        store.Write([Record("zeta"), Record("alpha"), Record("mid")]);

        var projector = new PluginPortalProjector(store);

        Assert.Equal(["alpha", "mid", "zeta"], projector.List().Select(r => r.Name));
    }

    [Fact]
    public void List_projects_the_installed_record_fields_the_portal_renders()
    {
        var store = new PluginStateStore(_root);
        var record = Record("alpha") with
        {
            ManifestVersion = "2.1.0",
            Reference = "v2",
            ResolvedVersion = "0123456789abcdef",
            Files = ["a.md", "b/c.md"],
        };
        store.Write([record]);
        MaterialiseFiles(record);

        var row = Assert.Single(new PluginPortalProjector(store).List());

        Assert.Equal("alpha", row.Name);
        Assert.Equal("2.1.0", row.ManifestVersion);
        Assert.Equal("v2", row.Reference);
        Assert.Equal("0123456789abcdef", row.ResolvedVersion);
        Assert.Equal(2, row.FileCount);
        Assert.True(row.UpdatesEnabled);
    }

    [Fact]
    public void Find_returns_null_for_a_plugin_that_is_not_installed()
    {
        var store = new PluginStateStore(_root);
        store.Write([Record("alpha")]);

        Assert.Null(new PluginPortalProjector(store).Find("nope"));
    }

    [Fact]
    public void Find_returns_null_for_a_blank_name_rather_than_throwing()
    {
        var store = new PluginStateStore(_root);
        store.Write([Record("alpha")]);

        Assert.Null(new PluginPortalProjector(store).Find("   "));
    }

    [Fact]
    public void An_enabled_plugin_reports_an_unknown_update_state_because_the_source_was_not_probed()
    {
        var store = new PluginStateStore(_root);
        var record = Record("alpha");
        store.Write([record]);
        MaterialiseFiles(record);

        var row = Assert.Single(new PluginPortalProjector(store).List());

        // Not "Current": claiming currency without probing the source would be a claim, not a
        // finding, and would be indistinguishable from a real up-to-date answer.
        Assert.Equal(PluginUpdateState.Unknown, row.UpdateState);
        Assert.Null(row.AvailableVersion);
    }

    [Fact]
    public void A_pinned_plugin_reports_pinned_rather_than_unknown()
    {
        var store = new PluginStateStore(_root);
        var record = Record("alpha") with { UpdatesEnabled = false };
        store.Write([record]);
        MaterialiseFiles(record);

        var row = Assert.Single(new PluginPortalProjector(store).List());

        // "Pinned" is the complete answer for a plugin whose source is deliberately never probed,
        // not a placeholder for a check that has yet to run.
        Assert.Equal(PluginUpdateState.Pinned, row.UpdateState);
        Assert.False(row.UpdatesEnabled);
    }

    [Fact]
    public void Content_with_every_recorded_file_present_is_unverified_not_verified()
    {
        var store = new PluginStateStore(_root);
        var record = Record("alpha") with { Files = ["one.md", "nested/two.md"] };
        store.Write([record]);
        MaterialiseFiles(record);

        var row = Assert.Single(new PluginPortalProjector(store).List());

        // Presence is all this slice can attest; content hashing arrives with #2682. Reporting
        // Verified on presence alone would overstate what was actually checked.
        Assert.Equal(PluginTrustState.Unverified, row.TrustState);
        Assert.Contains("Content hashes are not yet catalogued", row.TrustDetail);
    }

    [Fact]
    public void A_missing_recorded_file_is_reported_as_modified_and_names_the_file()
    {
        var store = new PluginStateStore(_root);
        var record = Record("alpha") with { Files = ["one.md", "nested/two.md"] };
        store.Write([record]);
        MaterialiseFiles(record);
        File.Delete(Path.Combine(_root, "alpha", "nested", "two.md"));

        var row = Assert.Single(new PluginPortalProjector(store).List());

        Assert.Equal(PluginTrustState.Modified, row.TrustState);
        Assert.Contains("nested/two.md", row.TrustDetail);
        Assert.Contains("1 recorded file(s) are missing", row.TrustDetail);
    }

    [Fact]
    public void A_missing_plugin_directory_is_reported_as_modified()
    {
        var store = new PluginStateStore(_root);
        store.Write([Record("alpha") with { Files = ["one.md"] }]);

        var row = Assert.Single(new PluginPortalProjector(store).List());

        Assert.Equal(PluginTrustState.Modified, row.TrustState);
        Assert.Contains("missing from disk", row.TrustDetail);
    }

    [Fact]
    public void A_record_with_no_files_cannot_be_attested_either_way()
    {
        var store = new PluginStateStore(_root);
        store.Write([Record("alpha") with { Files = [] }]);

        var row = Assert.Single(new PluginPortalProjector(store).List());

        Assert.Equal(PluginTrustState.Unverified, row.TrustState);
        Assert.Contains("integrity cannot be attested", row.TrustDetail);
    }

    [Fact]
    public void A_file_the_user_added_alongside_plugin_content_is_not_a_modification()
    {
        var store = new PluginStateStore(_root);
        var record = Record("alpha") with { Files = ["one.md"] };
        store.Write([record]);
        MaterialiseFiles(record);
        File.WriteAllText(Path.Combine(_root, "alpha", "my-notes.md"), "mine");

        var row = Assert.Single(new PluginPortalProjector(store).List());

        // Judged against the recorded set, never a directory scan - the same rule that keeps
        // removal from taking user content as collateral damage.
        Assert.Equal(PluginTrustState.Unverified, row.TrustState);
        Assert.DoesNotContain("missing", row.TrustDetail!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_row_serialises_with_the_camelCase_names_the_portal_binds_to()
    {
        var store = new PluginStateStore(_root);
        var record = Record("alpha") with { Files = ["one.md"] };
        store.Write([record]);
        MaterialiseFiles(record);

        var json = JsonSerializer.Serialize(Assert.Single(new PluginPortalProjector(store).List()));

        Assert.Contains("\"updatesEnabled\"", json);
        Assert.Contains("\"resolvedVersion\"", json);
        Assert.Contains("\"trustState\"", json);
        Assert.Contains("\"updateState\"", json);
        Assert.Contains("\"fileCount\"", json);
    }

    private static InstalledPlugin Record(string name) => new()
    {
        Name = name,
        Source = $"https://example.com/{name}.git",
        ResolvedVersion = "abcdef0123456789",
        InstalledAtUtc = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
        Files = ["one.md"],
    };

    private void MaterialiseFiles(InstalledPlugin plugin)
    {
        var directory = Path.Combine(_root, plugin.Name);
        foreach (var relative in plugin.Files)
        {
            var path = Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "content");
        }
    }
}
