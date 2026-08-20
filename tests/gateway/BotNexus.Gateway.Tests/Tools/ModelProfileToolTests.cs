using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Gateway.Prompts;
using BotNexus.Gateway.Tools;

namespace BotNexus.Gateway.Tests.Tools;

/// <summary>
/// Pins <c>model_profile</c> (#2436): the discovery tool that turns "not all models are equal" from
/// an assertion in the prompt into something an agent can query before editing a base instruction
/// file.
/// </summary>
public class ModelProfileToolTests
{
    private static ModelProfileTool Create(
        string? modelId = "claude-opus-4-8",
        string? providerId = "anthropic",
        ProviderCapabilities? capabilities = null,
        string? workspaceDir = "/ws",
        params string[] files) =>
        new(modelId, providerId, capabilities, workspaceDir, registry: null, listDirectory: _ => files);

    [Fact]
    public void ReportsFamilyAndVersionParsedByModelFamilyVersion()
    {
        var report = Create("gpt-5.6", "openai").BuildReport(null);

        Assert.Contains("- family: `gpt`", report, StringComparison.Ordinal);
        // #2374 owns version parsing; the tool must reuse it, not re-derive a second reading.
        Assert.Contains("major 5, minor 6", report, StringComparison.Ordinal);
    }

    [Fact]
    public void VersionIsAnchoredOnTheFamilyTokenExactlyAsTheVariantLadderAnchorsIt()
    {
        // claude-opus-4-8 hangs its version off "opus", not off "claude". PromptVariantRegistry.Resolve
        // reads the version against the FAMILY token, so under family `claude` this id carries no
        // version and the family+version rung is unreachable. The tool must report the same reading
        // the prompt path uses: a tool that claimed 4.8 here would tell an agent a rung applied that
        // never fired.
        var report = Create("claude-opus-4-8", "anthropic").BuildReport(null);

        Assert.Contains("- family: `claude`", report, StringComparison.Ordinal);
        Assert.Contains("not parseable", report, StringComparison.Ordinal);
    }

    [Fact]
    public void ReportsThatTheVersionRungDoesNotApplyWhenTheIdCarriesNoVersion()
    {
        // Silently omitting the version would read as "version 0" to an agent. Saying the rung does
        // not apply is the difference between a usable answer and a misleading one.
        var report = Create("claude-sonnet").BuildReport(null);

        Assert.Contains("not parseable", report, StringComparison.Ordinal);
        Assert.DoesNotContain("major", report, StringComparison.Ordinal);
    }

    [Fact]
    public void ReportsTheProvidersDeclaredCapabilitiesRatherThanDefaults()
    {
        var declared = new ProviderCapabilities(
            RecoversLeakedToolCallMarkup: true,
            SystemPromptPlacement: SystemPromptPlacement.DedicatedField,
            FramesStreamedTextDeltasWithCrlf: true);

        var report = Create(capabilities: declared).BuildReport(null);

        Assert.Contains("recoversLeakedToolCallMarkup: `True`", report, StringComparison.Ordinal);
        Assert.Contains("systemPromptPlacement: `DedicatedField`", report, StringComparison.Ordinal);
        Assert.Contains("framesStreamedTextDeltasWithCrlf: `True`", report, StringComparison.Ordinal);
    }

    [Fact]
    public void ReportsOnlyTheRungsThisTurnActuallyClimbs()
    {
        // The claude turn climbs default -> claude. A gpt turn must NOT be told the claude rung
        // applied to it: an agent that believes it inherited another family's overlay will draw
        // exactly the wrong conclusion about what is agnostic.
        var claude = Create("claude-opus-4-8", "anthropic").BuildReport(null);
        var gpt = Create("gpt-5.6", "openai").BuildReport(null);

        Assert.Contains($"`{ModelAwarenessSection.Id}`: default -> claude", claude, StringComparison.Ordinal);
        Assert.DoesNotContain($"`{ModelAwarenessSection.Id}`: default -> claude", gpt, StringComparison.Ordinal);
        Assert.Contains($"`{ModelAwarenessSection.Id}`: default -> gpt", gpt, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownFamilyReportsTheDefaultRungOnlyAndNeverNothing()
    {
        var report = Create("some-vendor-model", "openrouter").BuildReport(null);

        Assert.Contains("- family: `unknown`", report, StringComparison.Ordinal);
        Assert.Contains($"`{ModelAwarenessSection.Id}`: default", report, StringComparison.Ordinal);
    }

    [Fact]
    public void ListsExistingVariantFilesAndWhichOneResolvesForThisTurn()
    {
        var report = Create(
            "claude-opus-4-8",
            "anthropic",
            files: ["AGENTS.md", "AGENTS.claude.md", "AGENTS.gpt-5.md", "notes.md"])
            .BuildReport(null);

        Assert.Contains("`AGENTS.claude.md` (matches this model)", report, StringComparison.Ordinal);
        // A grammatically valid suffix naming another family must be listed but NOT marked matching.
        Assert.Contains("`AGENTS.gpt-5.md`", report, StringComparison.Ordinal);
        Assert.DoesNotContain("`AGENTS.gpt-5.md` (matches this model)", report, StringComparison.Ordinal);
        Assert.Contains("resolves to `AGENTS.claude.md` for this turn", report, StringComparison.Ordinal);
    }

    [Fact]
    public void SaysExplicitlyWhenNoVariantFilesExist()
    {
        // Absence must be stated. An empty section reads as "the scan failed", which would push an
        // agent back to guessing.
        var report = Create(files: ["AGENTS.md", "SOUL.md"]).BuildReport(null);

        Assert.Contains("no model-specific variant files exist yet", report, StringComparison.Ordinal);
    }

    [Fact]
    public void ScopesTheVariantScanToTheRequestedBaseFile()
    {
        var report = Create(files: ["AGENTS.claude.md", "SOUL.claude.md"]).BuildReport("SOUL.md");

        Assert.Contains("`SOUL.claude.md`", report, StringComparison.Ordinal);
        Assert.DoesNotContain("`AGENTS.claude.md`", report, StringComparison.Ordinal);
    }

    [Fact]
    public void EmitsTheFilenameGrammarSoAVariantIsNamedRightFirstTime()
    {
        // Acceptance clause: grammar documentation reachable from the tool output. The grammar comes
        // from ContextFileVariants so the doc cannot drift from the parser that enforces it.
        var report = Create().BuildReport(null);

        Assert.Contains(ContextFileVariants.GrammarPattern, report, StringComparison.Ordinal);
        Assert.Contains("AGENTS.gpt-5.md", report, StringComparison.Ordinal);
    }

    [Fact]
    public void DegradesToAStatedAbsenceWhenNoWorkspaceIsBound()
    {
        var report = new ModelProfileTool("claude-opus-4-8", "anthropic", workspaceDir: null).BuildReport(null);

        Assert.Contains("no workspace directory is bound", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteReturnsTheReportAsTextContent()
    {
        var result = await Create().ExecuteAsync("call-1", new Dictionary<string, object?>());

        var text = Assert.Single(result.Content).Value;
        Assert.Contains("## Model identity", text, StringComparison.Ordinal);
    }
}
