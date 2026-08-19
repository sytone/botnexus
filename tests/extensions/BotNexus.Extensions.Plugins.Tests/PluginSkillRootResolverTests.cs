using BotNexus.Extensions.Plugins.Lifecycle;

namespace BotNexus.Extensions.Plugins.Tests;

/// <summary>
/// Pins the plugin skills-root resolver (#2684). The resolver is the seam skill discovery uses to
/// learn which plugin directories may contribute skills, so its answer decides what reaches an
/// agent's context.
/// </summary>
public sealed class PluginSkillRootResolverTests : IDisposable
{
    private readonly string _root;

    public PluginSkillRootResolverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "botnexus-plugin-skillroot-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Resolve_NoStateFile_ReturnsEmpty()
    {
        Assert.Empty(PluginSkillRootResolver.Resolve(_root));
    }

    [Fact]
    public void Resolve_MissingPluginRoot_ReturnsEmpty()
    {
        Assert.Empty(PluginSkillRootResolver.Resolve(Path.Combine(_root, "does-not-exist")));
    }

    [Fact]
    public void Resolve_NullOrBlankRoot_ReturnsEmpty()
    {
        Assert.Empty(PluginSkillRootResolver.Resolve((string?)null));
        Assert.Empty(PluginSkillRootResolver.Resolve("   "));
    }

    [Fact]
    public void Resolve_InstalledPluginWithSkills_ReturnsItsSkillsDirectory()
    {
        Install("acme-tools", withSkills: true);

        var roots = PluginSkillRootResolver.Resolve(_root);

        Assert.Equal([Path.Combine(_root, "acme-tools", "skills")], roots);
    }

    [Fact]
    public void Resolve_InstalledPluginWithoutSkillsDirectory_IsOmitted()
    {
        Install("agents-only", withSkills: false);

        Assert.Empty(PluginSkillRootResolver.Resolve(_root));
    }

    [Fact]
    public void Resolve_UnrecordedDirectory_IsIgnored()
    {
        // A directory nothing installed has no provenance and no removal manifest; the installed
        // record - not the directory listing - is the authority on what may contribute skills.
        Directory.CreateDirectory(Path.Combine(_root, "smuggled", "skills"));
        new PluginStateStore(_root).Write([]);

        Assert.Empty(PluginSkillRootResolver.Resolve(_root));
    }

    [Fact]
    public void Resolve_MultiplePlugins_ReturnsRootsOrderedByPluginName()
    {
        Install("zeta", withSkills: true);
        Install("alpha", withSkills: true);
        Install("mid", withSkills: true);

        var roots = PluginSkillRootResolver.Resolve(_root);

        Assert.Equal(
            [
                Path.Combine(_root, "alpha", "skills"),
                Path.Combine(_root, "mid", "skills"),
                Path.Combine(_root, "zeta", "skills"),
            ],
            roots);
    }

    private void Install(string name, bool withSkills)
    {
        Directory.CreateDirectory(Path.Combine(_root, name));
        if (withSkills)
        {
            Directory.CreateDirectory(Path.Combine(_root, name, "skills"));
        }

        new PluginStateStore(_root).Upsert(new InstalledPlugin
        {
            Name = name,
            Source = $"https://example.com/{name}.git",
            ResolvedVersion = "abc123",
            InstalledAtUtc = DateTimeOffset.UnixEpoch,
            Files = [],
        });
    }
}
