using System.IO.Abstractions.TestingHelpers;
using BotNexus.Extensions.Skills;
using BotNexus.Extensions.Skills.Recording;

namespace BotNexus.Skills.Tests;

/// <summary>
/// Staging of proposed-but-not-installed skills, and the line that keeps them out of discovery.
/// </summary>
public sealed class SkillDraftStoreTests
{
    private const string AgentDir = "/home/agent/.botnexus/agents/test-agent";
    private const string AgentSkillsDir = AgentDir + "/skills";
    private const string WorkspaceSkillsDir = "/workspace/skills";
    private const string GlobalSkillsDir = "/home/agent/.botnexus/skills";

    private static SkillDraftStore Store(MockFileSystem fs, string? root = null)
        => new(
            root ?? SkillDraftStore.ResolveRoot(AgentDir),
            [AgentSkillsDir, WorkspaceSkillsDir, GlobalSkillsDir],
            fs);

    private static SkillDraft Draft(string name = "add-film") => new()
    {
        Name = name,
        Scope = "workspace",
        Content = "---\nname: " + name + "\ndescription: adds a film\n---\nAdd {{title}}.",
        Parameters = [new DraftParameter { Name = "title", ObservedValue = "Dune", Description = "The film." }],
        Steps = [new RecordedStepSummary { Ordinal = 1, ToolName = "bash", ArgumentKeys = ["command"] }],
        SessionId = "session-1",
        CreatedBy = "test-agent"
    };

    [Fact]
    public void SaveAndLoad_RoundTripsTheProposal()
    {
        var fs = new MockFileSystem();
        var store = Store(fs);

        store.Save(Draft());
        var loaded = store.TryLoad("add-film");

        loaded.ShouldNotBeNull();
        loaded.Parameters.Single().ObservedValue.ShouldBe("Dune");
        loaded.Steps.Single().ArgumentKeys.ShouldBe(["command"]);
    }

    [Fact]
    public void SaveAndLoad_PreservesTheConfirmationToken()
    {
        // review issues the token and confirm checks it, potentially in different turns. If the
        // token did not survive the round trip, every confirm would be refused as stale.
        var fs = new MockFileSystem();
        var store = Store(fs);
        var draft = Draft();

        store.Save(draft);

        store.TryLoad("add-film")!.ConfirmationToken.ShouldBe(draft.ConfirmationToken);
    }

    [Fact]
    public void ConfirmationToken_ChangesWhenTheContentChanges()
    {
        var original = Draft();
        var edited = original with { Content = original.Content + "\nThen verify it landed." };

        edited.ConfirmationToken.ShouldNotBe(original.ConfirmationToken);
    }

    [Fact]
    public void ConfirmationToken_IsUnaffectedByTheOrderParametersWereListedIn()
    {
        // Two proposals differing only in listing order are the same skill; telling an operator it
        // changed would train them to ignore the warning that matters.
        var a = new DraftParameter { Name = "a", ObservedValue = "1111", Description = "d" };
        var b = new DraftParameter { Name = "b", ObservedValue = "2222", Description = "d" };

        SkillDraft.ComputeToken("s", "workspace", "{{a}} {{b}}", [a, b])
            .ShouldBe(SkillDraft.ComputeToken("s", "workspace", "{{a}} {{b}}", [b, a]));
    }

    [Fact]
    public void TryLoad_ForAnUnknownName_IsNull()
    {
        Store(new MockFileSystem()).TryLoad("nothing-here").ShouldBeNull();
    }

    [Fact]
    public void TryLoad_ForACorruptDraft_ReadsAsAbsentRatherThanThrowing()
    {
        var fs = new MockFileSystem();
        var store = Store(fs);
        var path = Path.Combine(store.Root, "broken", SkillDraftStore.DraftFileName);
        fs.AddFile(path, new MockFileData("{ not json"));

        store.TryLoad("broken").ShouldBeNull();
    }

    [Fact]
    public void List_ReturnsPendingDraftsNewestFirst()
    {
        var fs = new MockFileSystem();
        var store = Store(fs);
        store.Save(Draft("older") with { CreatedAt = DateTimeOffset.UnixEpoch });
        store.Save(Draft("newer") with { CreatedAt = DateTimeOffset.UnixEpoch.AddDays(1) });

        store.List().Select(d => d.Name).ShouldBe(["newer", "older"]);
    }

    [Fact]
    public void Delete_RemovesTheDraftAndReportsWhetherThereWasOne()
    {
        var fs = new MockFileSystem();
        var store = Store(fs);
        store.Save(Draft());

        store.Delete("add-film").ShouldBeTrue();
        store.TryLoad("add-film").ShouldBeNull();
        store.Delete("add-film").ShouldBeFalse();
    }

    [Fact]
    public void Constructor_RefusesARootInsideASkillsDirectory()
    {
        // Checked rather than trusted. A draft is unreviewed content assembled from a transcript,
        // so a root that drifts inside a discovery tree is a silent widening — and this codebase
        // has already shipped four features whose only behaviour was an unnoticed fallback.
        var fs = new MockFileSystem();

        Should.Throw<ArgumentException>(() => Store(fs, root: AgentSkillsDir + "/drafts"));
    }

    [Fact]
    public void Constructor_RefusesASkillsDirectoryItself()
    {
        Should.Throw<ArgumentException>(() => Store(new MockFileSystem(), root: WorkspaceSkillsDir));
    }

    [Fact]
    public void Constructor_AcceptsASiblingWhoseNameMerelyStartsWithTheSkillsPath()
    {
        // "/…/skills-drafts" is NOT inside "/…/skills". A plain StartsWith on the undecorated
        // parent would say it is, and would reject the one layout that is actually correct.
        var fs = new MockFileSystem();

        Should.NotThrow(() => Store(fs, root: AgentSkillsDir + "-drafts"));
    }

    [Fact]
    public void ResolveRoot_SitsBesideTheAgentSkillsDirectory()
    {
        var root = SkillDraftStore.ResolveRoot(AgentDir);

        root.ShouldBe(Path.Combine(AgentDir, SkillDraftStore.DraftsDirectoryName));
        root.ShouldNotBe(AgentSkillsDir);
    }

    // ── the fence ────────────────────────────────────────────────────────────

    [Fact]
    public void ADraftIsNotDiscoverableAsASkill()
    {
        // The line this whole feature depends on: staged, unreviewed content must not be loadable.
        // This covers the PRIMARY guard — the draft root is somewhere nothing scans — and passes
        // whatever the draft file is called, which is why the name guard needs its own test below.
        var fs = new MockFileSystem();
        var store = Store(fs);
        store.Save(Draft());

        // Discovery is pointed at every real skills root for this agent.
        var discovered = SkillDiscovery.Discover(
            GlobalSkillsDir, AgentSkillsDir, WorkspaceSkillsDir, fs);

        discovered.ShouldBeEmpty();
    }

    [Fact]
    public void EvenPlacedInsideASkillsRoot_ADraftFileIsNotASkill()
    {
        // The second, independent reason drafts cannot load: discovery requires a file named
        // SKILL.md, and a draft is not one.
        //
        // The content here is a PERFECTLY VALID skill on purpose. An earlier version of this test
        // used the draft's own JSON, which discovery rejected for having no frontmatter — so it
        // passed for a reason that had nothing to do with the file name, and renaming the draft
        // file to SKILL.md left it green. Mutation testing caught that. With valid markdown, the
        // only thing standing between this file and the loader is its name.
        var fs = new MockFileSystem();
        fs.AddFile(
            Path.Combine(AgentSkillsDir, "smuggled", SkillDraftStore.DraftFileName),
            new MockFileData("""
                ---
                name: smuggled
                description: A complete, valid skill that must still not load.
                ---
                Do the thing.
                """));

        SkillDiscovery.Discover(GlobalSkillsDir, AgentSkillsDir, WorkspaceSkillsDir, fs).ShouldBeEmpty();
    }

    [Fact]
    public void TheDraftFileNameIsDeliberatelyNotSkillMd()
    {
        // Pins the second guard so an innocuous-looking rename cannot quietly remove it.
        SkillDraftStore.DraftFileName.ShouldNotBe("SKILL.md");
    }
}
