using System.Text;
using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Gateway.Prompts;

namespace BotNexus.Gateway.Tools;

/// <summary>
/// On-demand discovery tool (<c>model_profile</c>) that answers, from data rather than intuition,
/// the question the <c>model-awareness</c> prompt section poses (#2436): <i>which of these
/// instructions are mine specifically, and which belong to everyone?</i>
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a tool and not more prompt text.</b> The instruction alone leaves the agent guessing what
/// variants exist. An agent that can SEE it is running on <c>gpt-5</c>, that its provider declares
/// no leaked-tool-call recovery, and that <c>AGENTS.claude.md</c> already exists beside
/// <c>AGENTS.md</c>, is positioned to ask "is this rule true for everyone, or just for me?" and
/// answer it. This is also where the #2432 capability contract becomes actionable: an agent can
/// learn a transport quirk BEFORE relying on the behaviour, rather than by reading a failure.
/// </para>
/// <para>
/// <b>Everything reported is read from the shipping machinery, never re-derived.</b> The resolved
/// rungs come from <see cref="PromptVariantRegistry.Declarations"/> — the same frozen corpus the
/// prompt path resolves against — the version comes from <see cref="ModelFamilyVersion"/> (#2374),
/// the family from <see cref="ModelFamilyDetector"/>, the on-disk variants from
/// <see cref="ContextFileVariants"/> (#2435), and the capabilities from the provider's own
/// <see cref="ProviderCapabilities"/> declaration. A second copy of any of these would drift and
/// would then be confidently wrong, which is worse for a tool an agent consults instead of probing.
/// </para>
/// <para>
/// The filename grammar is emitted with the output so an agent authoring its first variant gets the
/// naming right the first time; a grammatically invalid suffix is not a variant at all and is
/// silently never read, which is the least debuggable failure this feature can produce.
/// </para>
/// </remarks>
public sealed class ModelProfileTool : IAgentTool
{
    /// <summary>The instruction files whose variants are worth reporting without being asked.</summary>
    private static readonly string[] DefaultBaseFiles = ["AGENTS.md", "SOUL.md", "WORLD.md", "IDENTITY.md", "USER.md", "TOOLS.md"];

    private readonly string? _modelId;
    private readonly string? _providerId;
    private readonly ProviderCapabilities _capabilities;
    private readonly string? _workspaceDir;
    private readonly PromptVariantRegistry _registry;
    private readonly Func<string, IReadOnlyList<string>> _listDirectory;

    /// <summary>
    /// Initializes a new instance of the <see cref="ModelProfileTool"/> class.
    /// </summary>
    /// <param name="modelId">The active model id, e.g. <c>claude-opus-5</c>.</param>
    /// <param name="providerId">The active provider instance key, e.g. <c>github-copilot</c>.</param>
    /// <param name="capabilities">The capabilities declared by the serving provider (#2432).</param>
    /// <param name="workspaceDir">The agent workspace directory scanned for variant files.</param>
    /// <param name="registry">The frozen variant registry; defaults to <see cref="PromptVariantRegistry.Shared"/>.</param>
    /// <param name="listDirectory">
    /// Bare-file-name enumerator for a directory. Injected so the variant scan is testable without a
    /// real filesystem; defaults to a non-throwing directory listing.
    /// </param>
    public ModelProfileTool(
        string? modelId,
        string? providerId,
        ProviderCapabilities? capabilities = null,
        string? workspaceDir = null,
        PromptVariantRegistry? registry = null,
        Func<string, IReadOnlyList<string>>? listDirectory = null)
    {
        _modelId = modelId;
        _providerId = providerId;
        _capabilities = capabilities ?? ProviderCapabilities.Default;
        _workspaceDir = workspaceDir;
        _registry = registry ?? PromptVariantRegistry.Shared;
        _listDirectory = listDirectory ?? SafeListDirectory;
    }

    /// <inheritdoc />
    public string Name => "model_profile";

    /// <inheritdoc />
    public string Label => "Model Profile";

    /// <summary>Content source classification for turn-taint accumulation (#2519). Platform-local data.</summary>
    public string ContentSource => ToolContentSource.Local;

    /// <inheritdoc />
    public Tool Definition => new(
        Name,
        "Report which model family and version you are running on, the capabilities your provider declares, "
        + "which instruction-variant rungs resolved for this turn, and which model-specific instruction files "
        + "exist in the workspace. Call this BEFORE editing a base instruction file (AGENTS.md, SOUL.md, "
        + "WORLD.md) so you can decide whether your change is agnostic (belongs in the base file) or "
        + "model-specific (belongs in a variant). Also use it to check a provider capability before relying "
        + "on the behaviour.",
        JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "baseFile": {
                  "type": "string",
                  "description": "Optional bare file name, e.g. 'AGENTS.md'. When given, only variants of that base file are listed. When omitted, the well-known instruction files are reported."
                }
              }
            }
            """).RootElement.Clone());

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(arguments);
    }

    /// <inheritdoc />
    public Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new AgentToolResult(
            [new AgentToolContent(AgentToolContentType.Text, BuildReport(ReadString(arguments, "baseFile")))]));
    }

    /// <summary>
    /// Builds the profile report. Internal so tests assert the CONTENT an agent actually reads
    /// rather than a parallel projection of it.
    /// </summary>
    /// <param name="requestedBaseFile">An optional single base file to scope the variant scan to.</param>
    /// <returns>The rendered report.</returns>
    internal string BuildReport(string? requestedBaseFile)
    {
        var family = ModelFamilyDetector.GetModelFamily(_modelId, _providerId);
        var sb = new StringBuilder();

        sb.AppendLine("## Model identity");
        sb.AppendLine($"- model: `{_modelId ?? "(unset)"}`");
        sb.AppendLine($"- provider: `{_providerId ?? "(unset)"}`");
        sb.AppendLine($"- family: `{family}`");
        ModelVersion? version = ModelFamilyVersion.TryParse(_modelId, family, out var parsed) ? parsed : null;
        sb.AppendLine(version is { } known
            ? $"- version: `{known}` (major {known.Major}, minor {known.Minor})"
            : "- version: not parseable from this model id — the family+version rung of the ladder does not apply.");

        sb.AppendLine();
        sb.AppendLine("## Declared provider capabilities (#2432)");
        sb.AppendLine("These are DECLARED by the provider, not probed. A capability that is false means the platform applies no workaround for it.");
        sb.AppendLine($"- recoversLeakedToolCallMarkup: `{_capabilities.RecoversLeakedToolCallMarkup}`");
        sb.AppendLine($"- systemPromptPlacement: `{_capabilities.SystemPromptPlacement}`");

        AppendResolvedRungs(sb, family, version);
        AppendVariantFiles(sb, requestedBaseFile);
        AppendGrammar(sb);

        return sb.ToString().TrimEnd();
    }

    private void AppendResolvedRungs(StringBuilder sb, string family, ModelVersion? version)
    {
        sb.AppendLine();
        sb.AppendLine("## Prompt-section variant rungs (#2433)");
        sb.AppendLine("Resolution is least-specific first: `default`, then `family`, then `family+version`. Each rung OVERLAYS the one beneath it by stable rule id.");

        var sections = _registry.Declarations
            .Select(static declaration => declaration.SectionId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static id => id, StringComparer.Ordinal)
            .ToList();

        if (sections.Count == 0)
        {
            sb.AppendLine("- (no sections declare variants)");
            return;
        }

        foreach (var sectionId in sections)
        {
            var applied = _registry.Declarations
                .Where(declaration => string.Equals(declaration.SectionId, sectionId, StringComparison.OrdinalIgnoreCase))
                .Where(declaration => AppliesToThisTurn(declaration, family, version))
                .Select(DescribeRung)
                .ToList();

            sb.AppendLine($"- `{sectionId}`: {string.Join(" -> ", applied)}");
        }
    }

    /// <summary>
    /// True when <paramref name="declaration"/> is a rung the CURRENT turn actually climbs. This
    /// mirrors <see cref="PromptVariantRegistry.Resolve"/>'s ladder exactly: the default always
    /// applies, a family rung applies on a family match, and a family+version rung applies only when
    /// the model id carried a parseable version equal to the declared one.
    /// </summary>
    private static bool AppliesToThisTurn(PromptVariantDeclaration declaration, string family, ModelVersion? version)
    {
        if (declaration.IsDefault)
            return true;

        if (!string.Equals(declaration.Family, family, StringComparison.OrdinalIgnoreCase))
            return false;

        return declaration.Version is null || (version is not null && declaration.Version.Value == version.Value);
    }

    private static string DescribeRung(PromptVariantDeclaration declaration) =>
        declaration.IsDefault
            ? "default"
            : declaration.Version is null
                ? $"{declaration.Family}"
                : $"{declaration.Family}@{declaration.Version.Value}";

    private void AppendVariantFiles(StringBuilder sb, string? requestedBaseFile)
    {
        sb.AppendLine();
        sb.AppendLine("## Model-specific instruction files (#2435)");

        if (string.IsNullOrWhiteSpace(_workspaceDir))
        {
            sb.AppendLine("- (no workspace directory is bound to this session, so no variant scan was performed)");
            return;
        }

        var present = _listDirectory(_workspaceDir);
        var baseFiles = string.IsNullOrWhiteSpace(requestedBaseFile)
            ? DefaultBaseFiles
            : [requestedBaseFile.Trim()];

        var reported = false;
        foreach (var baseFile in baseFiles)
        {
            var variants = present
                .Where(name => ContextFileVariants.TryParse(name, out var suffix)
                               && suffix is not null
                               && string.Equals(suffix.BaseFileName, baseFile, StringComparison.OrdinalIgnoreCase))
                .OrderBy(static name => name, StringComparer.Ordinal)
                .ToList();

            if (variants.Count == 0)
                continue;

            var winner = ContextFileVariants.Resolve(present, baseFile, _modelId, _providerId);
            var rendered = variants.Select(name =>
            {
                ContextFileVariants.TryParse(name, out var suffix);
                var matches = suffix is not null && ContextFileVariants.Score(suffix, _modelId, _providerId) is not null;
                return $"`{name}`{(matches ? " (matches this model)" : "")}";
            });

            sb.AppendLine($"- `{baseFile}`: {string.Join(", ", rendered)} — resolves to `{winner}` for this turn.");
            reported = true;
        }

        if (!reported)
            sb.AppendLine("- no model-specific variant files exist yet; every instruction file resolves to its base.");
    }

    private static void AppendGrammar(StringBuilder sb)
    {
        sb.AppendLine();
        sb.AppendLine("## Authoring a variant");
        sb.AppendLine("Name the file `<stem>.<suffix>.<ext>`, e.g. `AGENTS.gpt-5.md`, `SOUL.claude-opus-4-8.md`.");
        sb.AppendLine($"The suffix must match the grammar `{ContextFileVariants.GrammarPattern}`: lowercase alphanumerics with single hyphens between tokens.");
        sb.AppendLine("Name tokens come first, version components last; a name token after a numeric one is not a variant shape.");
        sb.AppendLine("A file whose suffix violates the grammar, or names a family this model does not belong to, is NOT a variant: it is never read and the base file is used instead. Failing to a visible base beats silently loading the wrong instructions.");
        sb.AppendLine("Put a rule in the BASE file only when it is true for every model. A rule that is true only for your family belongs in a variant.");
    }

    private static IReadOnlyList<string> SafeListDirectory(string directory)
    {
        try
        {
            return Directory.Exists(directory)
                ? [.. Directory.EnumerateFiles(directory).Select(Path.GetFileName).OfType<string>()]
                : [];
        }
        catch
        {
            // A discovery tool must never take down a turn because the workspace was unreadable.
            return [];
        }
    }

    private static string? ReadString(IReadOnlyDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
            return null;

        return value switch
        {
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            JsonElement { ValueKind: JsonValueKind.Null } => null,
            JsonElement element => element.ToString(),
            _ => value.ToString()
        };
    }
}
