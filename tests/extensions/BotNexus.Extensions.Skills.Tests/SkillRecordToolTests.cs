using System.IO.Abstractions.TestingHelpers;
using BotNexus.Agent.Core.Types;
using BotNexus.Extensions.Skills;
using BotNexus.Extensions.Skills.Recording;

namespace BotNexus.Skills.Tests;

/// <summary>
/// The propose-and-confirm cycle end to end: steps → propose → review → confirm.
/// </summary>
/// <remarks>
/// The property these tests exist to pin is that nothing reaches a skills directory before confirm,
/// and that confirm installs the same bytes review displayed. Everything else is detail.
/// </remarks>
public sealed class SkillRecordToolTests
{
    private const string AgentDir = "/home/agent/.botnexus/agents/test-agent";
    private const string AgentSkillsDir = AgentDir + "/skills";
    private const string WorkspaceSkillsDir = "/workspace/skills";
    private const string GlobalSkillsDir = "/home/agent/.botnexus/skills";

    private const string Body = """
        ---
        name: add-film
        description: Adds a film to Radarr and reports when it lands.
        ---
        1. POST {{title}} to Radarr at http://nas:7878.
        2. Poll the queue until it clears.
        """;

    private static IReadOnlyList<RecordedStep> Run() =>
    [
        new()
        {
            Ordinal = 1,
            ToolName = "bash",
            ArgumentsJson = """{"command":"curl -s http://nas:7878/api/v3/movie -d '{\"title\":\"Dune\"}'"}""",
            Timestamp = DateTimeOffset.UnixEpoch
        }
    ];

    private static object[] Parameters() =>
    [
        new Dictionary<string, object?>
        {
            ["name"] = "title",
            ["observedValue"] = "Dune",
            ["description"] = "The film to add; different every run."
        }
    ];

    private sealed record Harness(
        SkillRecordTool Tool,
        SkillDraftStore Drafts,
        MockFileSystem FileSystem);

    private static Harness Build(
        bool withTrace = true,
        SkillsConfig? config = null,
        IReadOnlyList<RecordedStep>? steps = null)
    {
        var fs = new MockFileSystem();
        var settings = config ?? new SkillsConfig { AllowSkillCreation = true };

        var writer = new SkillManagerTool(
            AgentSkillsDir, WorkspaceSkillsDir, GlobalSkillsDir, settings, fs);

        var drafts = new SkillDraftStore(
            SkillDraftStore.ResolveRoot(AgentDir),
            [AgentSkillsDir, WorkspaceSkillsDir, GlobalSkillsDir],
            fs);

        ISessionTraceSource? trace = withTrace
            ? new SkillRecorderTests.FakeTraceSource(steps ?? Run())
            : null;

        return new Harness(new SkillRecordTool(writer, drafts, trace, settings, "test-agent"), drafts, fs);
    }

    private static Dictionary<string, object?> Args(string action, params (string Key, object? Value)[] extra)
    {
        var args = new Dictionary<string, object?> { ["action"] = action };
        foreach (var (key, value) in extra)
            args[key] = value;
        return args;
    }

    private static string Text(AgentToolResult result)
        => string.Join(string.Empty, result.Content.Select(c => c.Value));

    private static async Task<string> ProposeAsync(Harness harness, string? content = null)
        => Text(await harness.Tool.ExecuteAsync("call", Args(
            "propose",
            ("name", "add-film"),
            ("content", content ?? Body),
            ("parameters", Parameters()))));

    // ── steps ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Steps_ReadsBackTheRunFromPersistedHistory()
    {
        var text = Text(await Build().Tool.ExecuteAsync("call", Args("steps")));

        text.ShouldContain("bash");
        text.ShouldContain("Recorded steps");
    }

    [Fact]
    public async Task Steps_WithNothingRecorded_ExplainsThatThisTurnIsNotInHistoryYet()
    {
        // Found on the live instance, not in a unit test: a turn that ran bash and then asked for
        // its own steps got ZERO, because a call is not written to session history until the turn
        // completes. Asking again on the next turn returned it. Reading history rather than the
        // in-flight run is deliberate — it is what makes a recording survive compaction — so the
        // empty state has to explain the timing instead of reading as "the feature does nothing".
        var harness = Build(steps: []);

        var text = Text(await harness.Tool.ExecuteAsync("call", Args("steps")));

        text.ShouldNotStartWith("Error:");
        text.ShouldContain("THIS turn");
        text.ShouldContain("next turn");
    }

    [Fact]
    public async Task Propose_WithNothingRecorded_GivesTheSameExplanationAndStagesNothing()
    {
        // The agent that ignores the hint from `steps` hits this instead. It must fail CLOSED and
        // say the same thing, or the obvious recovery is to invent the steps.
        var harness = Build(steps: []);

        var text = await ProposeAsync(harness);

        text.ShouldStartWith("Error:");
        text.ShouldContain("next turn");
        harness.Drafts.TryLoad("add-film").ShouldBeNull();
    }

    // ── propose ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Propose_StagesADraftAndInstallsNothing()
    {
        var harness = Build();

        var text = await ProposeAsync(harness);

        text.ShouldNotStartWith("Error:");
        harness.Drafts.TryLoad("add-film").ShouldNotBeNull();
        // The skill itself must not exist yet. This is the whole point of the staging step.
        harness.FileSystem.Directory
            .Exists(Path.Combine(WorkspaceSkillsDir, "add-film"))
            .ShouldBeFalse();
    }

    [Fact]
    public async Task Propose_WithAFabricatedValue_StagesNothing()
    {
        var harness = Build();

        var text = Text(await harness.Tool.ExecuteAsync("call", Args(
            "propose",
            ("name", "add-film"),
            ("content", Body),
            ("parameters", new object[]
            {
                new Dictionary<string, object?>
                {
                    ["name"] = "title",
                    ["observedValue"] = "Arrival",
                    ["description"] = "The film."
                }
            }))));

        text.ShouldStartWith("Error:");
        harness.Drafts.TryLoad("add-film").ShouldBeNull();
    }

    [Fact]
    public async Task Propose_RefusesAHandWrittenParametersBlock()
    {
        // Two sources of truth for the parameters would disagree the first time an operator renames
        // a slot at review, and the installed skill would declare something nobody confirmed.
        var withBlock = """
            ---
            name: add-film
            description: Adds a film.
            parameters:
              title: something else entirely
            ---
            Add {{title}}.
            """;

        var text = await ProposeAsync(Build(), withBlock);

        text.ShouldStartWith("Error:");
        text.ShouldContain("generated from the 'parameters' argument");
    }

    // ── review ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Review_ShowsTheParametersTheLiteralsAndTheExactFileToBeInstalled()
    {
        var harness = Build();
        await ProposeAsync(harness);

        var text = Text(await harness.Tool.ExecuteAsync("call", Args("review", ("name", "add-film"))));

        text.ShouldContain("Parameters proposed");
        text.ShouldContain("Values kept FIXED");
        text.ShouldContain("Dune");
        text.ShouldContain(harness.Drafts.TryLoad("add-film")!.ConfirmationToken);
    }

    [Fact]
    public async Task Review_ForAnUnknownDraft_IsAnError()
    {
        Text(await Build().Tool.ExecuteAsync("call", Args("review", ("name", "not-a-draft"))))
            .ShouldStartWith("Error:");
    }

    // ── confirm ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Confirm_WithTheReviewedToken_InstallsTheSkillAndClearsTheDraft()
    {
        var harness = Build();
        await ProposeAsync(harness);
        var token = harness.Drafts.TryLoad("add-film")!.ConfirmationToken;

        var text = Text(await harness.Tool.ExecuteAsync("call", Args(
            "confirm", ("name", "add-film"), ("token", token))));

        text.ShouldNotStartWith("Error:");
        harness.FileSystem.File
            .Exists(Path.Combine(WorkspaceSkillsDir, "add-film", "SKILL.md"))
            .ShouldBeTrue();
        harness.Drafts.TryLoad("add-film").ShouldBeNull();
    }

    [Fact]
    public async Task Confirm_WritesTheParameterBlockThatWasConfirmed()
    {
        var harness = Build();
        await ProposeAsync(harness);
        var token = harness.Drafts.TryLoad("add-film")!.ConfirmationToken;

        await harness.Tool.ExecuteAsync("call", Args("confirm", ("name", "add-film"), ("token", token)));

        var installed = harness.FileSystem.File.ReadAllText(
            Path.Combine(WorkspaceSkillsDir, "add-film", "SKILL.md"));

        // Parsed rather than string-matched: what matters is that the loader sees the declaration.
        SkillParser.Parse("add-film", installed, "/x", SkillSource.Workspace)
            .Parameters.Keys.ShouldBe(["title"]);
    }

    [Fact]
    public async Task Confirm_InstallsExactlyWhatReviewDisplayed()
    {
        // review and confirm build the file through one function, so this cannot drift into two
        // implementations that merely agree today.
        var harness = Build();
        await ProposeAsync(harness);
        var draft = harness.Drafts.TryLoad("add-film")!;

        await harness.Tool.ExecuteAsync("call", Args(
            "confirm", ("name", "add-film"), ("token", draft.ConfirmationToken)));

        harness.FileSystem.File
            .ReadAllText(Path.Combine(WorkspaceSkillsDir, "add-film", "SKILL.md"))
            .ShouldBe(SkillRecordTool.BuildInstallableContent(draft));
    }

    [Fact]
    public async Task Confirm_WithoutAToken_IsRefused()
    {
        var harness = Build();
        await ProposeAsync(harness);

        var text = Text(await harness.Tool.ExecuteAsync("call", Args("confirm", ("name", "add-film"))));

        text.ShouldStartWith("Error:");
        harness.FileSystem.Directory.Exists(Path.Combine(WorkspaceSkillsDir, "add-film")).ShouldBeFalse();
    }

    [Fact]
    public async Task Confirm_WithATokenFromAnEarlierVersionOfTheDraft_IsRefused()
    {
        // The operator reviewed something else. Installing anyway would make review decorative.
        var harness = Build();
        await ProposeAsync(harness);
        var staleToken = harness.Drafts.TryLoad("add-film")!.ConfirmationToken;

        await ProposeAsync(harness, Body + "\n3. Also notify the operator.");

        var text = Text(await harness.Tool.ExecuteAsync("call", Args(
            "confirm", ("name", "add-film"), ("token", staleToken))));

        text.ShouldStartWith("Error:");
        text.ShouldContain("changed since it was reviewed");
        harness.FileSystem.Directory.Exists(Path.Combine(WorkspaceSkillsDir, "add-film")).ShouldBeFalse();
    }

    [Fact]
    public async Task Confirm_WhenTheWritePathRefuses_KeepsTheDraft()
    {
        // The draft is the only copy of work the operator has already reviewed; discarding it on a
        // refusal would make them redo the review to fix a name collision.
        var harness = Build();
        await ProposeAsync(harness);
        var token = harness.Drafts.TryLoad("add-film")!.ConfirmationToken;
        harness.FileSystem.AddFile(
            Path.Combine(WorkspaceSkillsDir, "add-film", "SKILL.md"),
            new MockFileData("---\nname: add-film\ndescription: already here\n---\nbody"));

        var text = Text(await harness.Tool.ExecuteAsync("call", Args(
            "confirm", ("name", "add-film"), ("token", token))));

        text.ShouldStartWith("Error:");
        harness.Drafts.TryLoad("add-film").ShouldNotBeNull();
    }

    // ── discard / list ───────────────────────────────────────────────────────

    [Fact]
    public async Task Discard_RemovesTheDraftWithoutInstallingAnything()
    {
        var harness = Build();
        await ProposeAsync(harness);

        Text(await harness.Tool.ExecuteAsync("call", Args("discard", ("name", "add-film"))))
            .ShouldNotStartWith("Error:");
        harness.Drafts.TryLoad("add-film").ShouldBeNull();
        harness.FileSystem.Directory.Exists(Path.Combine(WorkspaceSkillsDir, "add-film")).ShouldBeFalse();
    }

    [Fact]
    public async Task List_NamesPendingDrafts()
    {
        var harness = Build();
        await ProposeAsync(harness);

        Text(await harness.Tool.ExecuteAsync("call", Args("list"))).ShouldContain("add-film");
    }

    // ── gates ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WithNoTraceSource_EveryRecordingActionRefusesRatherThanProposingUnverified()
    {
        // The documented failure mode on this codebase is a feature whose only behaviour is a
        // silent "does nothing" fallback. An unwired trace must be a visible refusal, because a
        // proposal checked against an empty trace is a proposal checked against nothing.
        var harness = Build(withTrace: false);

        Text(await harness.Tool.ExecuteAsync("call", Args("steps"))).ShouldStartWith("Error:");
        (await ProposeAsync(harness)).ShouldStartWith("Error:");
        harness.Drafts.TryLoad("add-film").ShouldBeNull();
    }

    [Fact]
    public async Task WhenSkillCreationIsDisabled_EveryActionRefuses()
    {
        var harness = Build(config: new SkillsConfig { AllowSkillCreation = false });

        foreach (var action in new[] { "steps", "propose", "review", "confirm", "discard", "list" })
            Text(await harness.Tool.ExecuteAsync("call", Args(action, ("name", "add-film"))))
                .ShouldStartWith("Error:");
    }

    [Fact]
    public async Task AnUnknownActionIsNamedRatherThanIgnored()
    {
        Text(await Build().Tool.ExecuteAsync("call", Args("replay")))
            .ShouldContain("Unknown action");
    }
}
