using BotNexus.Gateway.Configuration;
using BotNexus.Cli.Commands.Doctor.Generated;

namespace BotNexus.Cli.Commands.Doctor;

/// <summary>
/// Checks that <c>gateway.extensions</c> block exists and is enabled.
/// </summary>
[DoctorCheck(Id = "extensions-block", Suite = DoctorSuite.Config, Order = 0)]
public sealed class ExtensionsBlockCheck : IConfigCheck
{
    private const string ExtensionsPath = "gateway.extensions";
    internal const string ExtensionsEnabledPath = "gateway.extensions.enabled";

    public string Id => "extensions-block";
    public string Description => "gateway.extensions block is absent or has extensions disabled.";
    public string FixDescription => "Add gateway.extensions = { enabled: true }";

    public bool IsApplicable(ConfigDocument config)
    {
        if (!config.HasObject(ExtensionsPath))
            return true;

        // Present - only flag when explicitly disabled. A missing `enabled` key means the block
        // exists and the platform default (on) applies, which is not a gap.
        return config.GetBool(ExtensionsEnabledPath) is false;
    }

    public void Apply(ConfigDocument config) => config.Set(ExtensionsEnabledPath, true);
}

/// <summary>
/// Checks that <c>gateway.extensions.defaults["botnexus-skills"]</c> is present and enabled.
/// </summary>
[DoctorCheck(Id = "skills-world-default", Suite = DoctorSuite.Config, Order = 1)]
public sealed class SkillsWorldDefaultCheck : IConfigCheck
{
    private const string SkillsEntryPath = "gateway.extensions.defaults.botnexus-skills";
    private const string SkillsEnabledPath = "gateway.extensions.defaults.botnexus-skills.enabled";

    public string Id => "skills-world-default";
    public string Description => "Skills extension has no world-level default in gateway.extensions.defaults.";
    public string FixDescription => "Add gateway.extensions.defaults[\"botnexus-skills\"].enabled = true";

    public bool IsApplicable(ConfigDocument config)
    {
        // Absent, or present as something other than a settings object, both mean "no usable
        // world-level default". Only an object carrying an explicit false is a deliberate opt-out
        // that this check must still report.
        if (!config.HasObject(SkillsEntryPath))
            return true;

        return config.GetBool(SkillsEnabledPath) is false;
    }

    public void Apply(ConfigDocument config)
    {
        config.Set(ExtensionsBlockCheck.ExtensionsEnabledPath, true);
        config.Set(SkillsEnabledPath, true);
    }
}

/// <summary>
/// Recommends enabling the dev-mode browser-Origin guard (#1931) when the gateway runs keyless
/// (development mode). The guard defends the auto-granted <c>gateway-dev</c> admin identity
/// against DNS-rebind / CSRF from a malicious web origin, but ships OFF by default so it can
/// never lock a keyless operator out of the UI on restart. This check surfaces the opt-in.
/// <para>
/// Only applicable when NO API key is configured (keyless dev mode) and the
/// <c>FeatureManagement.GatewayDevOriginEnforcement</c> flag is not already enabled. Applying the
/// fix seeds <c>gateway.cors.allowedOrigins</c> with the localhost default (only if unset, so an
/// operator's existing origins are preserved) and turns the flag on. Operators who reach the UI
/// over a non-localhost origin (LAN hostname, reverse proxy, netbird) must add that origin to
/// <c>gateway.cors.allowedOrigins</c> before enabling, or they will be locked out.
/// </para>
/// </summary>
[DoctorCheck(Id = "devmode-origin-enforcement", Suite = DoctorSuite.Config, Order = 6)]
public sealed class DevOriginEnforcementCheck : IConfigCheck
{
    /// <summary>Feature-flag name; the single declaration shared with ApiKeyGatewayAuthHandler (#2767).</summary>
    private const string FeatureName = FeatureFlags.GatewayDevOriginEnforcement;
    private const string DefaultOrigin = GatewayDefaults.LoopbackListenUrl;
    private const string LegacyApiKeyPath = "apiKey";
    private const string ApiKeysPath = "gateway.apiKeys";
    private const string AllowedOriginsPath = "gateway.cors.allowedOrigins";
    private static readonly string FeaturePath = $"{FeatureFlags.SectionName}.{FeatureName}";

    public string Id => "devmode-origin-enforcement";
    public string Description =>
        "Gateway runs keyless (dev mode) with the browser-Origin guard disabled - the gateway-dev admin identity is reachable from any web origin (DNS-rebind/CSRF risk).";
    public string FixDescription =>
        "Enable FeatureManagement.GatewayDevOriginEnforcement and seed gateway.cors.allowedOrigins = [\"" + DefaultOrigin + "\"]. WARNING: if you reach the UI over a non-localhost origin (LAN hostname / reverse proxy / netbird), add that origin to gateway.cors.allowedOrigins FIRST or you will be locked out.";

    public bool IsApplicable(ConfigDocument config)
    {
        // Only relevant in keyless dev mode - a configured API key path is unaffected by this guard.
        if (HasAnyApiKey(config))
            return false;

        // Already enabled -> nothing to recommend.
        return !IsFeatureEnabled(config);
    }

    public void Apply(ConfigDocument config)
    {
        // Seed a localhost allow-list only if none exists, preserving any origins the operator set.
        if (!config.HasNonEmptyList(AllowedOriginsPath))
            config.Set(AllowedOriginsPath, new[] { DefaultOrigin });

        // Turn the flag on under the FeatureManagement section (Microsoft.FeatureManagement schema).
        config.Set(FeaturePath, true);
    }

    private static bool HasAnyApiKey(ConfigDocument config)
    {
        if (config.TryGetString(LegacyApiKeyPath, out var legacy) && !string.IsNullOrWhiteSpace(legacy))
            return true;

        return config.CountEntries(ApiKeysPath) > 0;
    }

    private static bool IsFeatureEnabled(ConfigDocument config)
        // Microsoft.FeatureManagement accepts either a bool literal or an object with an
        // EnabledFor filter list; we only treat a literal `true` as "already enabled" for the
        // purposes of this recommendation.
        => config.GetBool(FeaturePath) is true;
}

/// <summary>
/// Checks that the top-level <c>cron</c> block exists with scheduler enabled.
/// </summary>
[DoctorCheck(Id = "cron-enabled", Suite = DoctorSuite.Config, Order = 2)]
public sealed class CronCheck : IConfigCheck
{
    private const string CronPath = "cron";

    public string Id => "cron-enabled";
    public string Description => "cron scheduler block is absent from config.";
    public string FixDescription => "Add cron = { enabled: true, tickIntervalSeconds: 60 }";

    public bool IsApplicable(ConfigDocument config) => !config.HasObject(CronPath);

    public void Apply(ConfigDocument config)
    {
        if (config.HasObject(CronPath))
            return;

        config.SetMap(CronPath, new ConfigValueMap()
            .Set("enabled", true)
            .Set("tickIntervalSeconds", 60));
    }
}

/// <summary>
/// Checks that <c>gateway.compaction.summarizationModel</c> is not set to an expensive reasoning
/// model. Reasoning models (claude-opus-4.6, o3, gpt-5) are overkill for summarization and may
/// return empty responses when the thinking parameter is misconfigured.
/// </summary>
[DoctorCheck(Id = "compaction-model", Suite = DoctorSuite.Config, Order = 4)]
public sealed class CompactionModelCheck : IConfigCheck
{
    public string Id => "compaction-model";
    public string Description => "gateway.compaction.summarizationModel uses an expensive reasoning model — may fail or waste tokens.";
    public string FixDescription => "Change gateway.compaction.summarizationModel to \"claude-haiku-4.5\" (fast, cheap, reliable for summarization)";

    private static readonly string[] ExpensiveModels =
    [
        "claude-opus-4.6", "claude-opus-4-6", "o3", "o4-mini",
        "gpt-5", "gpt-5.2", "claude-opus-4"
    ];

    public bool IsApplicable(ConfigDocument config)
    {
        var model = GetSummarizationModel(config);
        if (string.IsNullOrWhiteSpace(model)) return false; // no model set — different concern
        return ExpensiveModels.Any(e => model.Contains(e, StringComparison.OrdinalIgnoreCase));
    }

    public void Apply(ConfigDocument config)
        => config.Set(SummarizationModelPath, "claude-haiku-4.5");

    // #2764: read through the canonical path, never a hand-rolled root lookup. The setting binds at
    // gateway.compaction, so the old root["compaction"] read was always null and this guard could
    // never fire — a rule structurally incapable of firing reads exactly like a clean pass. #2887
    // then removed the ability to express the wrong traversal at all.
    private static string? GetSummarizationModel(ConfigDocument config)
        => config.TryGetString(SummarizationModelPath, out var model) ? model : null;

    internal const string SummarizationModelPath = "gateway.compaction.summarizationModel";
}

/// <summary>
/// Checks that <c>gateway.compaction.summarizationModel</c> is configured at all.
/// Without an explicit model, the compactor falls back to a default waterfall
/// which may pick an expensive or unavailable model.
/// </summary>
[DoctorCheck(Id = "compaction-model-missing", Suite = DoctorSuite.Config, Order = 5)]
public sealed class CompactionModelMissingCheck : IConfigCheck
{
    public string Id => "compaction-model-missing";
    public string Description => "gateway.compaction.summarizationModel is not configured — compactor will use default model waterfall.";
    public string FixDescription => "Set gateway.compaction.summarizationModel to \"claude-haiku-4.5\"";

    // #2764: a root-level "compaction" block binds to nothing, so its presence is NOT evidence the
    // model is configured. Only the canonical path counts — otherwise this check reported a
    // correctly configured platform as broken on every run.
    public bool IsApplicable(ConfigDocument config)
        => !config.TryGetString(CompactionModelCheck.SummarizationModelPath, out var model)
           || string.IsNullOrWhiteSpace(model);

    public void Apply(ConfigDocument config)
        => config.Set(CompactionModelCheck.SummarizationModelPath, "claude-haiku-4.5");
}

/// <summary>
/// Checks that <c>agents.defaults.memory</c> block is present.
/// </summary>
[DoctorCheck(Id = "memory-agent-default", Suite = DoctorSuite.Config, Order = 3)]
public sealed class MemoryAgentDefaultCheck : IConfigCheck
{
    private const string AgentsPath = "agents";
    private const string MemoryPath = "agents.defaults.memory";

    public string Id => "memory-agent-default";
    public string Description => "agents.defaults.memory block is absent — memory indexing will not be enabled by default.";
    public string FixDescription => "Add agents.defaults.memory = { enabled: true, indexing: \"auto\" }";

    public bool IsApplicable(ConfigDocument config)
    {
        // No agents block at all - not our concern; init generates one and the other checks cover it.
        if (!config.HasObject(AgentsPath))
            return false;

        return !config.HasObject(MemoryPath);
    }

    public void Apply(ConfigDocument config)
    {
        if (!config.HasObject(AgentsPath) || config.HasObject(MemoryPath))
            return;

        config.SetMap(MemoryPath, new ConfigValueMap()
            .Set("enabled", true)
            .Set("indexing", "auto"));
    }
}

/// <summary>
/// Reports a tool-output backstop set far enough above the compaction bloat threshold that a
/// single tool result can pass the backstop and then, on its own, force a compaction.
/// </summary>
/// <remarks>
/// <para>
/// Two settings, chosen independently, that only make sense read together.
/// <c>gateway.toolOutputBudget.maxBytes</c> bounds one tool result (256 KiB by default, chosen to
/// sit above every first-party per-tool cap so the backstop never retunes one).
/// <c>gateway.compaction.largestEntryBytesThreshold</c> declares a single entry to be dead weight
/// worth compacting (64 KiB by default).
/// </para>
/// <para>
/// A result landing between the two is billed in full at the uncached rate, then immediately makes
/// the session eligible for compaction -- which costs a summarisation call and resets the prompt
/// cache for everything behind it. Paying three times to carry something the next step throws away.
/// </para>
/// <para>
/// The fix aligns the backstop down rather than raising the bloat threshold, because truncation is
/// recoverable -- the model pages the rest back through <c>tool_output_continue</c> -- whereas a
/// bloat trigger raised past the backstop can no longer fire on a single entry at all. It is a
/// recommendation, applied only by <c>doctor config --fix</c>: an agent that leans on large shell
/// output may legitimately want the opposite direction, and that is an operator's call.
/// </para>
/// </remarks>
[DoctorCheck(Id = "tool-output-budget-vs-compaction", Suite = DoctorSuite.Config, Order = 7)]
public sealed class ToolOutputBudgetCompactionCoherenceCheck : IConfigCheck
{
    internal const string BackstopEnabledPath = "gateway.toolOutputBudget.enabled";
    internal const string BackstopMaxBytesPath = "gateway.toolOutputBudget.maxBytes";
    internal const string BloatThresholdPath = "gateway.compaction.largestEntryBytesThreshold";

    /// <summary>Platform default for the central tool-output backstop (256 KiB).</summary>
    internal const int DefaultBackstopBytes = 256 * 1024;

    /// <summary>Platform default for the per-entry compaction bloat trigger (64 KiB).</summary>
    internal const int DefaultBloatThresholdBytes = 64 * 1024;

    public string Id => "tool-output-budget-vs-compaction";

    public string Description =>
        "gateway.toolOutputBudget.maxBytes sits above gateway.compaction.largestEntryBytesThreshold — " +
        "a tool result between the two is paid for in full and then forces a compaction on its own.";

    public string FixDescription =>
        "Lower gateway.toolOutputBudget.maxBytes to gateway.compaction.largestEntryBytesThreshold " +
        "(truncation stays recoverable through tool_output_continue)";

    public bool IsApplicable(ConfigDocument config)
    {
        // A disabled backstop is a deliberate opt-out of bounding tool output at all. Reporting a
        // band inside a bound the operator has switched off would be noise.
        if (config.GetBool(BackstopEnabledPath) is false)
            return false;

        var backstop = config.GetInt(BackstopMaxBytesPath) ?? DefaultBackstopBytes;

        // Zero or less disables the backstop even when Enabled is true, matching
        // ToolOutputBudgetConfig's documented convention.
        if (backstop <= 0)
            return false;

        var bloatThreshold = config.GetInt(BloatThresholdPath) ?? DefaultBloatThresholdBytes;

        // A non-positive threshold means the byte-based compaction trigger is switched off, so
        // there is no band and nothing to report.
        if (bloatThreshold <= 0)
            return false;

        return backstop > bloatThreshold;
    }

    public void Apply(ConfigDocument config)
    {
        var bloatThreshold = config.GetInt(BloatThresholdPath) ?? DefaultBloatThresholdBytes;
        config.Set(BackstopMaxBytesPath, bloatThreshold);
    }
}
