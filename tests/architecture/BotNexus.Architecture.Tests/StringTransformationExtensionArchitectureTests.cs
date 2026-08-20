using System.Text.RegularExpressions;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// #2925: a general-purpose string-to-string transformation belongs on <c>this string</c>, not on a
/// helper class whose name you have to already know. This fence stops the codebase drifting back:
/// a NEWLY added <c>public static class</c> exposing a string-to-string method that is not declared
/// as an extension (and is not a documented forwarding shim) fails the build.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a frozen baseline rather than zero.</b> The acceptance criterion is about newly added
/// classes. The pre-existing static helpers are enumerated in <see cref="s_baseline"/> and are
/// permitted to stay; anything not on that list is a new violation. The list is frozen - it may
/// shrink as helpers migrate, never grow. <see cref="Baseline_HasNoStaleEntries"/> enforces the
/// shrink-only direction, so the fence cannot be silently relaxed by appending to it.
/// </para>
/// <para>
/// <b>What is deliberately NOT caught (AC4).</b> Static factories and parsers that return a richer
/// domain type - <c>ModelFamilyVersion.TryParse</c>, <c>ConversationOrigin.ParseKind</c>,
/// <c>SkillParser.Parse</c>, <c>Agent365ChannelAddress.Encode</c> - are matched only on a
/// <c>string</c>/<c>string?</c> RETURN type, so a factory returning anything else is structurally
/// outside this fence. A static factory is the idiomatic constructor for a strong type;
/// <c>"gpt-4o".ToModelFamilyVersion()</c> would be worse than what it replaces. Naming consistency
/// for that group is #2926 and strong types for the string-keyed domain policies are #2927 -
/// neither is dragged in here. <see cref="Fence_Exempts_StaticFactory_Returning_NonStringType"/>
/// pins that exemption so a future widening of the pattern breaks a named test rather than quietly
/// expanding scope.
/// </para>
/// </remarks>
public sealed class StringTransformationExtensionArchitectureTests
{
    /// <summary>
    /// A <c>public static</c> method returning <c>string</c>/<c>string?</c> whose first parameter is
    /// a <c>string</c>/<c>string?</c> that is NOT declared <c>this</c>. The return-type anchor is
    /// what implements the AC4 exemption: a factory returning a domain type never matches.
    /// </summary>
    private static readonly Regex s_nonExtensionStringTransform = new(
        @"public\s+static\s+string\??\s+(?<name>\w+)\s*\(\s*(?!this\b)string\??\s+\w+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex s_publicStaticClass = new(
        @"public\s+static\s+(partial\s+)?class\s+(?<name>\w+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Marker a retained static entry point must carry to be permitted (AC2's "documented
    /// forwarding shim"). Prose, not an attribute, because the point is that a human reading the
    /// method is told where the real implementation lives.
    /// </summary>
    private const string ShimMarker = "Documented forwarding shim (#2925)";

    /// <summary>
    /// Pre-existing static string-to-string helpers, frozen on 2026-08-19. Shrink-only.
    /// Format: <c>relative/path.cs::MethodName</c>.
    /// </summary>
    // SHRINK-ONLY. Entries may be DELETED as helpers migrate to `this string` extensions; a new
    // entry here is a fence relaxation, not a fix. Baseline_HasNoStaleEntries enforces the direction.
    private static readonly string[] s_baselineEntries =
    [
        "agent/BotNexus.Agent.Core/Loop/ToolOutputBudget.cs::ContinuationGuidance",
        "agent/BotNexus.Agent.Core/Loop/ToolOutputBudget.cs::NextLinkNotice",
        "agent/BotNexus.Agent.Core/Tools/AnsiStripper.cs::Strip",
        "agent/BotNexus.Agent.Core/Tools/SkillScriptPreflight.cs::Validate",
        "agent/BotNexus.Agent.Core/Tools/ToolContentSource.cs::Normalize",
        "agent/BotNexus.Agent.Providers.Copilot/CopilotEndpointAllowlist.cs::SanitiseApiEndpoint",
        "agent/BotNexus.Agent.Providers.Core/Embeddings/HostedEmbeddingFingerprint.cs::Derive",
        "agent/BotNexus.Agent.Providers.Core/EnvironmentApiKeys.cs::DescribeSourceVariable",
        "agent/BotNexus.Agent.Providers.Core/EnvironmentApiKeys.cs::GetApiKey",
        "agent/BotNexus.Agent.Providers.Core/ProviderHttpErrorHelper.cs::RedactDiagnosticText",
        "agent/BotNexus.Agent.Providers.Core/Registry/ModelPreflight.cs::FormatList",
        "agent/BotNexus.Agent.Providers.Core/Streaming/CompletionsStreamEngine.cs::ExtractProviderErrorMessage",
        "agent/BotNexus.Agent.Providers.Core/Streaming/ResponsesStreamPrimitives.cs::ComposeToolCallId",
        "agent/BotNexus.Agent.Providers.Core/Streaming/StreamAssemblyConformance.cs::Reconcile",
        "agent/BotNexus.Agent.Providers.Core/Utilities/ShortHash.cs::Generate",
        "domain/BotNexus.Domain.Wire/GraphemeSafeTruncation.cs::Truncate",
        "domain/BotNexus.Domain.Wire/TextualMimeType.cs::BoundText",
        "domain/BotNexus.Domain/Gateway/Models/ToolGlyphs.cs::ForTool",
        "domain/BotNexus.Domain/Gateway/Security/ActorPseudonym.cs::For",
        "domain/BotNexus.Domain/Paths/HomePathExpander.cs::Expand",
        "domain/BotNexus.Domain/Paths/HomePathExpander.cs::ExpandRequired",
        "domain/BotNexus.Domain/Text/UntrustedContentSanitizer.cs::Sanitize",
        "domain/BotNexus.Domain/World/WorldSentinel.cs::DescribeMismatch",
        "domain/BotNexus.Domain/World/WorldSentinel.cs::Serialize",
        "extensions/BotNexus.Extensions.BrowserTools/BrowserSnapshotEnvelope.cs::Wrap",
        "extensions/BotNexus.Extensions.Channels.Matrix/MatrixMessageFormatter.cs::ToHtml",
        "extensions/BotNexus.Extensions.Channels.SignalR.BlazorClient.Core/Services/AnsiStripper.cs::Strip",
        "extensions/BotNexus.Extensions.Channels.SignalR.BlazorClient.Core/Services/CanvasSubmitGuards.cs::ComposeContent",
        "extensions/BotNexus.Extensions.Channels.SignalR.BlazorClient.Core/Services/CanvasSubmitGuards.cs::TryNormalise",
        "extensions/BotNexus.Extensions.Channels.SignalR.BlazorClient.Core/Services/ConversationLabel.cs::DerivedLabel",
        "extensions/BotNexus.Extensions.Channels.SignalR.BlazorClient.Core/Services/ConversationLabel.cs::DisplayTitle",
        "extensions/BotNexus.Extensions.Channels.SignalR.BlazorClient.Core/Services/ConversationLabel.cs::Truncate",
        "extensions/BotNexus.Extensions.Channels.SignalR.BlazorClient.Core/Services/PortalPreferences.cs::Normalize",
        "extensions/BotNexus.Extensions.Channels.SignalR.BlazorClient.Core/Services/SurrogateSafeText.cs::SurrogateSafeTruncate",
        "extensions/BotNexus.Extensions.Channels.SignalR.BlazorClient/Components/ToolDescriptionFormatter.cs::FormatDescription",
        "extensions/BotNexus.Extensions.Mcp/Plugins/PluginScopedServerName.cs::Scope",
        "gateway/BotNexus.Cron/CronAlertTarget.cs::UnresolvableMessage",
        "gateway/BotNexus.Cron/CronAlertTarget.cs::UnverifiableMessage",
        "gateway/BotNexus.Cron/CronModelPreflight.cs::Summarize",
        "gateway/BotNexus.Gateway.Abstractions/Extensions/ExtensionMeters.cs::InstrumentName",
        "gateway/BotNexus.Gateway.Abstractions/Extensions/ExtensionMeters.cs::ValidateExtensionId",
        "gateway/BotNexus.Gateway.Abstractions/Models/AgentUserMessageComposer.cs::AppendNonImageAttachments",
        "gateway/BotNexus.Gateway.Api/Configuration/ConfigSchemaBuilder.cs::Humanize",
        "gateway/BotNexus.Gateway.Api/Export/ExportFileName.cs::Build",
        "gateway/BotNexus.Gateway.Api/Export/ExportFileName.cs::Slugify",
        "gateway/BotNexus.Gateway.Api/RequestLogText.cs::Safe",
        "gateway/BotNexus.Gateway.Api/RequestLogText.cs::SafePath",
        "gateway/BotNexus.Gateway.Channels/AssistantTextSanitizer.cs::Sanitize",
        "gateway/BotNexus.Gateway.Channels/AssistantTextSanitizer.cs::StripLeakedToolCalls",
        "gateway/BotNexus.Gateway.Channels/AssistantTextSanitizer.cs::StripThinkingTags",
        "gateway/BotNexus.Gateway.Channels/RuntimeContextRedactor.cs::Strip",
        "gateway/BotNexus.Gateway.Configuration/BotNexusHome.cs::ResolveDataPath",
        "gateway/BotNexus.Gateway.Configuration/BotNexusHome.cs::ResolveHomePath",
        "gateway/BotNexus.Gateway.Configuration/ConfigSectionGuard.cs::FormatRejection",
        "gateway/BotNexus.Gateway.Configuration/SubAgentWorkspaceRootResolver.cs::Resolve",
        "gateway/BotNexus.Gateway.Conversations/ConversationInputValidator.cs::ValidateInstructions",
        "gateway/BotNexus.Gateway.Conversations/ConversationInputValidator.cs::ValidatePurpose",
        "gateway/BotNexus.Gateway.Conversations/ConversationInputValidator.cs::ValidateTitle",
        "gateway/BotNexus.Gateway.Prompts/ContextFileOrdering.cs::GetBasename",
        "gateway/BotNexus.Gateway.Prompts/ContextFileOrdering.cs::NormalizePath",
        "gateway/BotNexus.Gateway.Prompts/ContextFileVariants.cs::GetBaseFileName",
        "gateway/BotNexus.Gateway.Prompts/ModelFamilyDetector.cs::GetModelFamily",
        "gateway/BotNexus.Gateway.Prompts/PromptText.cs::NormalizeStructuredSection",
        "gateway/BotNexus.Gateway.Sessions/SessionFileNames.cs::HistoryFileName",
        "gateway/BotNexus.Gateway.Sessions/SessionFileNames.cs::MetadataFileName",
        "gateway/BotNexus.Gateway.Sessions/SessionFileNames.cs::SanitizeSessionId",
        "gateway/BotNexus.Gateway.Sessions/TranscriptSecretRedactor.cs::Redact",
        "gateway/BotNexus.Gateway.Telemetry.Abstractions/BotNexusMeters.cs::InstrumentName",
        "gateway/BotNexus.Gateway.Webhooks/WebhookSecretHelper.cs::ComputeSignature",
        "gateway/BotNexus.Gateway/Agents/AgentModelPreflight.cs::ValidateResolvable",
        "gateway/BotNexus.Gateway/Agents/SubAgentSummaryNormalizer.cs::Normalize",
        "gateway/BotNexus.Gateway/Isolation/SandboxSkillPathRewriter.cs::RewriteMultiplePaths",
        "gateway/BotNexus.Gateway/Isolation/SandboxSkillPathRewriter.cs::RewritePaths",
        "gateway/BotNexus.Gateway/Streaming/StreamingSessionHelper.cs::TruncateToolResult",
        "gateway/BotNexus.Gateway/Tools/CanvasDeepLink.cs::ResolveBaseUrl",
        "gateway/BotNexus.Memory/Models/MemoryProvenance.cs::Normalize",
        "gateway/BotNexus.Memory/Tools/MemoryQuarantine.cs::ApplyMarker",
        "gateway/BotNexus.Memory/Tools/MemoryQuarantine.cs::BuildMarker",
        "gateway/BotNexus.Memory/TranscriptTurnFormat.cs::Encode",
        "gateway/BotNexus.Memory/TranscriptTurnFormat.cs::Quote",
        "gateway/BotNexus.Tools/Utils/ContentToken.cs::Compute",
        "gateway/BotNexus.Tools/Utils/PathUtils.cs::GetRelativePath",
        "gateway/BotNexus.Tools/Utils/PathUtils.cs::NormalizePath",
        "gateway/BotNexus.Tools/Utils/PathUtils.cs::ResolvePath",
        "gateway/BotNexus.Tools/Utils/PathUtils.cs::SanitizePath",
        "persistence/BotNexus.Persistence.Sqlite/SqliteStoreIdentityGuard.cs::DeriveStoreKind",
    ];

    private static readonly HashSet<string> s_baseline =
        new(s_baselineEntries.Select(e => e.Replace('/', Path.DirectorySeparatorChar)), StringComparer.Ordinal);

    [Fact]
    public void NoNewPublicStaticClass_Exposes_A_NonExtension_StringToStringMethod()
    {
        var srcRoot = FindSourceRoot();
        var violations = new List<string>();

        foreach (var (relative, text) in EnumerateProductionCsFiles(srcRoot))
        {
            if (!s_publicStaticClass.IsMatch(text)) continue;

            var previousMatchEnd = 0;
            foreach (Match match in s_nonExtensionStringTransform.Matches(text))
            {
                var key = $"{relative}::{match.Groups["name"].Value}";
                var isShim = IsDocumentedShim(text, match.Index, previousMatchEnd);
                previousMatchEnd = match.Index + match.Length;

                if (s_baseline.Contains(key)) continue;
                if (isShim) continue;
                violations.Add(key);
            }
        }

        violations.ShouldBeEmpty(
            "#2925: a general-purpose string-to-string transformation must be a `this string` " +
            "extension method, not a static helper you have to know the class name to find. " +
            "Either declare the first parameter `this string`, or - if a public API break would " +
            "otherwise occur - keep the static entry point as a thin forwarder whose doc comment " +
            "carries the marker \"" + ShimMarker + "\".\n" +
            "Static factories returning a non-string domain type are exempt by construction and " +
            "are NOT what this is asking you to change (see #2926/#2927).\n" +
            "New violations:\n  " + string.Join("\n  ", violations));
    }

    /// <summary>
    /// The baseline may shrink as helpers migrate; it must never point at something that no longer
    /// exists, because a stale entry is an exemption nobody is watching.
    /// </summary>
    [Fact]
    public void Baseline_HasNoStaleEntries()
    {
        var srcRoot = FindSourceRoot();
        var live = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (relative, text) in EnumerateProductionCsFiles(srcRoot))
        {
            if (!s_publicStaticClass.IsMatch(text)) continue;
            foreach (Match match in s_nonExtensionStringTransform.Matches(text))
                live.Add($"{relative}::{match.Groups["name"].Value}");
        }

        var stale = s_baseline.Where(entry => !live.Contains(entry)).OrderBy(x => x, StringComparer.Ordinal).ToList();

        stale.ShouldBeEmpty(
            "#2925: these baseline exemptions no longer match any source. Delete them - the " +
            "baseline is shrink-only and a stale entry silently re-permits a future violation at " +
            "the same path.\nStale:\n  " + string.Join("\n  ", stale));
    }

    /// <summary>
    /// AC4, pinned: a static factory whose return type is a domain type is outside this fence, so
    /// #2926/#2927 scope cannot be dragged in by a future widening of the pattern.
    /// </summary>
    [Fact]
    public void Fence_Exempts_StaticFactory_Returning_NonStringType()
    {
        s_nonExtensionStringTransform.IsMatch(
            "public static ModelFamilyVersion Parse(string value)").ShouldBeFalse();
        s_nonExtensionStringTransform.IsMatch(
            "public static bool TryParse(string value, out ModelFamilyVersion result)").ShouldBeFalse();
        s_nonExtensionStringTransform.IsMatch(
            "public static ConversationOrigin ParseKind(string raw)").ShouldBeFalse();
        s_nonExtensionStringTransform.IsMatch(
            "public static IReadOnlyList<string> SplitAll(string value)").ShouldBeFalse();
    }

    /// <summary>Vacuity guard: the pattern must actually fire on the shape it claims to police.</summary>
    [Fact]
    public void Fence_Regex_MatchesItsTargetShape()
    {
        s_nonExtensionStringTransform.IsMatch(
            "public static string Sanitize(string value)").ShouldBeTrue();
        s_nonExtensionStringTransform.IsMatch(
            "public static string? SafeTruncate(string? value, int maxLength)").ShouldBeTrue();

        // The whole point: declaring it an extension makes it compliant.
        s_nonExtensionStringTransform.IsMatch(
            "public static string Sanitize(this string value)").ShouldBeFalse();
        s_nonExtensionStringTransform.IsMatch(
            "public static string? SafeTruncate(this string? value, int maxLength)").ShouldBeFalse();

        // Non-string first parameter is a different shape entirely.
        s_nonExtensionStringTransform.IsMatch(
            "public static string Render(int count, string label)").ShouldBeFalse();
    }

    /// <summary>The shim escape hatch must require the marker, not merely a nearby doc comment.</summary>
    [Fact]
    public void ShimExemption_Requires_TheDocumentedMarker()
    {
        const string withMarker = """
            /// <remarks>
            /// Documented forwarding shim (#2925). Implementation moved to the extension.
            /// </remarks>
            public static string Sanitize(string value) => value.Sanitize();
            """;
        const string withoutMarker = """
            /// <remarks>
            /// Some other justification that is not the marker.
            /// </remarks>
            public static string Sanitize(string value) => Impl(value);
            """;

        var hit = s_nonExtensionStringTransform.Match(withMarker);
        hit.Success.ShouldBeTrue();
        IsDocumentedShim(withMarker, hit.Index).ShouldBeTrue();

        var miss = s_nonExtensionStringTransform.Match(withoutMarker);
        miss.Success.ShouldBeTrue();
        IsDocumentedShim(withoutMarker, miss.Index).ShouldBeFalse();

        // A marked shim must not launder an unmarked violation that follows it in the same file.
        var combined = withMarker + "\n" + withoutMarker;
        var matches = s_nonExtensionStringTransform.Matches(combined);
        matches.Count.ShouldBe(2);
        IsDocumentedShim(combined, matches[0].Index).ShouldBeTrue();
        IsDocumentedShim(combined, matches[1].Index, matches[0].Index + matches[0].Length).ShouldBeFalse();
    }

    /// <summary>
    /// True when the marker appears in the doc-comment block immediately preceding the match. The
    /// window is bounded both by a fixed size and by the END OF THE PREVIOUS DECLARATION, so a
    /// marker on an earlier shim cannot launder an unmarked violation that follows it.
    /// </summary>
    private static bool IsDocumentedShim(string text, int matchIndex, int previousMatchEnd = 0)
    {
        const int DocCommentWindow = 900;
        var start = Math.Max(previousMatchEnd, Math.Max(0, matchIndex - DocCommentWindow));
        return text.AsSpan(start, matchIndex - start).Contains(ShimMarker, StringComparison.Ordinal);
    }

    private static IEnumerable<(string Relative, string Text)> EnumerateProductionCsFiles(string srcRoot)
    {
        foreach (var path in Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
                path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return (ToRelative(srcRoot, path), File.ReadAllText(path));
        }
    }

    private static string ToRelative(string srcRoot, string fullPath)
    {
        var full = Path.GetFullPath(fullPath);
        var root = Path.GetFullPath(srcRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full[root.Length..] : full;
    }

    private static string FindSourceRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "BotNexus.slnx")))
        {
            current = current.Parent;
        }
        current.ShouldNotBeNull("Could not locate repo root from " + AppContext.BaseDirectory);
        return Path.Combine(current!.FullName, "src");
    }
}
