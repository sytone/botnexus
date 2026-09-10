using BotNexus.Extensions.Plugins.Lifecycle;

namespace BotNexus.Extensions.Plugins.Tests;

/// <summary>
/// Tests the registry of places to look for plugins.
/// </summary>
/// <remarks>
/// A source is a place to look, not something installed — so the store's job is to survive being
/// read by a portal on every page load and written by an operator occasionally, without ever
/// taking the plugins page down. Hence the emphasis here on the degenerate reads: missing file,
/// empty file, corrupt file.
/// </remarks>
public sealed class MarketplaceSourceStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "botnexus-source-tests", Guid.NewGuid().ToString("N"));

    public MarketplaceSourceStoreTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leaked temp directory is not worth failing a test over.
        }
    }

    private MarketplaceSourceStore NewStore() => new(_root);

    private static MarketplaceSource Source(string name, string url) => new()
    {
        Name = name,
        Url = url,
        AddedAtUtc = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero),
    };

    [Fact]
    public void No_file_yet_reads_as_no_sources_rather_than_an_error()
    {
        var store = NewStore();

        store.Read().ShouldBeEmpty();
    }

    [Fact]
    public void An_empty_file_reads_as_no_sources()
    {
        var store = NewStore();
        File.WriteAllText(store.StatePath, "   ");

        store.Read().ShouldBeEmpty();
    }

    // A hand-edited file that no longer parses must not take the plugins page down. Sources are
    // re-addable; the plugins they led to are installed independently and unaffected.
    [Fact]
    public void A_corrupt_file_degrades_to_empty_instead_of_throwing()
    {
        var store = NewStore();
        File.WriteAllText(store.StatePath, "{ this is not json");

        store.Read().ShouldBeEmpty();
    }

    [Fact]
    public void A_written_source_round_trips()
    {
        var store = NewStore();

        store.Upsert(Source("owner-repo", "https://github.com/owner/repo.git") with
        {
            Kind = "plugin",
            Offerings = [new MarketplaceOffering
            {
                Name = "my-plugin",
                Url = "https://github.com/owner/repo.git",
                Version = "1.0.0",
                CarriesExtension = true,
            }],
        });

        var read = store.Read().ShouldHaveSingleItem();
        read.Name.ShouldBe("owner-repo");
        read.Kind.ShouldBe("plugin");
        read.Offerings.ShouldHaveSingleItem().CarriesExtension.ShouldBeTrue();
    }

    [Fact]
    public void Upsert_replaces_rather_than_duplicates()
    {
        var store = NewStore();
        store.Upsert(Source("owner-repo", "https://github.com/owner/repo.git"));

        store.Upsert(Source("owner-repo", "https://github.com/owner/repo.git") with { LastError = "unreachable" });

        store.Read().ShouldHaveSingleItem().LastError.ShouldBe("unreachable");
    }

    [Fact]
    public void Delete_reports_whether_it_removed_anything()
    {
        var store = NewStore();
        store.Upsert(Source("owner-repo", "https://github.com/owner/repo.git"));

        store.Delete("owner-repo").ShouldBeTrue();
        store.Delete("owner-repo").ShouldBeFalse();
        store.Read().ShouldBeEmpty();
    }

    // The write is a temp file plus a replace, so an interrupted write cannot leave a truncated
    // document. Assert the temp file does not survive - a stray .tmp beside the state is how you
    // find out the replace never happened.
    [Fact]
    public void Writing_leaves_no_temporary_file_behind()
    {
        var store = NewStore();

        store.Upsert(Source("owner-repo", "https://github.com/owner/repo.git"));

        File.Exists(store.StatePath).ShouldBeTrue();
        File.Exists(store.StatePath + ".tmp").ShouldBeFalse();
    }

    // Paste a link, do not invent an identifier.
    [Theory]
    [InlineData("https://github.com/bradbor23/botnexus-agent-builder.git", "bradbor23-botnexus-agent-builder")]
    [InlineData("https://github.com/bradbor23/botnexus-agent-builder", "bradbor23-botnexus-agent-builder")]
    [InlineData("https://github.com/bradbor23/botnexus-agent-builder/", "bradbor23-botnexus-agent-builder")]
    [InlineData("git@github.com:owner/Repo.Name.git", "owner-repo-name")]
    public void A_name_is_derived_from_the_url(string url, string expected)
    {
        MarketplaceSourceStore.DeriveName(url).ShouldBe(expected);
    }

    // Owner AND repo, because two people's "botnexus-plugins" are not the same source and must
    // not collide into one another in the registry.
    [Fact]
    public void Two_owners_with_the_same_repo_name_do_not_collide()
    {
        var a = MarketplaceSourceStore.DeriveName("https://github.com/alice/botnexus-plugins.git");
        var b = MarketplaceSourceStore.DeriveName("https://github.com/bob/botnexus-plugins.git");

        a.ShouldNotBe(b);
    }

    [Fact]
    public void A_derived_name_is_always_a_usable_slug()
    {
        foreach (var url in new[]
        {
            "https://github.com/Owner/UPPER_Case.Repo.git",
            "https://example.com/a//b///c.git",
            "https://github.com/x/y",
        })
        {
            var name = MarketplaceSourceStore.DeriveName(url);
            name.ShouldNotBeNullOrWhiteSpace();
            name.ShouldBe(name.ToLowerInvariant());
            name.ShouldNotStartWith("-");
            name.ShouldNotEndWith("-");
            name.ShouldNotContain("--");
        }
    }
}
