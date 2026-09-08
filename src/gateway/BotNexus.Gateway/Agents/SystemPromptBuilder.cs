using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Prompts;

namespace BotNexus.Gateway.Agents;

public enum PromptMode
{
    Full,
    Minimal,
    None
}

public sealed record ContextFile(string Path, string Content);

public sealed record RuntimeInfo
{
    public string? AgentId { get; init; }
    public string? Host { get; init; }
    public string? Os { get; init; }
    public string? Arch { get; init; }
    public string? Provider { get; init; }
    public string? Model { get; init; }
    public string? DefaultModel { get; init; }
    /// <summary>
    /// The effective context-window size applied to this run (#2796), or null for the provider
    /// default. Emitted on the runtime line so the window the agent reports cannot drift from the
    /// window AgentOptions actually applied.
    /// </summary>
    public int? ContextWindow { get; init; }
    public string? Shell { get; init; }
    public string? Channel { get; init; }
    public string? ClientKind { get; init; }
    public IReadOnlyList<string>? Capabilities { get; init; }
    public string? SessionId { get; init; }
    public string? SessionKey { get; init; }
}

/// <param name="RunStartedAt">
/// Start of the current recurring run, when the conversation is driven by one (cron). Lets the todo
/// section separate this run's agenda from earlier runs' minutes (#2984). <c>null</c> for every
/// interactive caller, which preserves the original rendering exactly.
/// </param>
public sealed record ConversationContext(string ConversationId, string Title, string? Purpose, string? Instructions = null, string? Todo = null, DateTimeOffset? RunStartedAt = null);

public sealed record SystemPromptParams
{
    public required string WorkspaceDir { get; init; }
    public string? ExtraSystemPrompt { get; init; }
    public IReadOnlyList<string>? ToolNames { get; init; }
    public string? UserTimezone { get; init; }
    public IReadOnlyList<ContextFile>? ContextFiles { get; init; }
    public string? HeartbeatPrompt { get; init; }
    public string? DocsPath { get; init; }
    public IReadOnlyList<string>? WorkspaceNotes { get; init; }
    public string? TtsHint { get; init; }
    public PromptMode PromptMode { get; init; } = PromptMode.Full;
    public RuntimeInfo? Runtime { get; init; }
    public IReadOnlyList<string>? ModelAliasLines { get; init; }
    public string? OwnerIdentity { get; init; }
    public bool ReasoningTagHint { get; init; }
    public string? ReasoningLevel { get; init; }
    public string? MemoryPromptInjection { get; init; }

    /// <summary>
    /// Whether the conversation this prompt serves is owner-private or shared with non-owner
    /// participants (issue #2846). Defaults to <see cref="ConversationScope.Private"/> so every
    /// existing caller renders an unchanged prompt. <see cref="ConversationScope.Shared"/>
    /// suppresses the memory-write guidance block, which would otherwise instruct the agent to
    /// consult and write owner-private memory that is not in its context.
    /// </summary>
    public ConversationScope Scope { get; init; } = ConversationScope.Private;
    public ConversationContext? ConversationContext { get; init; }

    /// <summary>
    /// Prompt contributors resolved from the host container (#3667). Empty for every caller that
    /// does not supply them, which renders an identical prompt to the pre-#3667 behaviour.
    /// </summary>
    /// <remarks>
    /// This is the seam that makes <see cref="IPromptContributor"/> reachable from production at
    /// all. Before #3667 the builder composed its pipeline exclusively from
    /// <c>Add(IPromptSection)</c>, so a contributor registered in DI was constructed and silently
    /// ignored. Threading them as a parameter rather than resolving DI inside the builder keeps
    /// <c>SystemPromptBuilder</c> a static pure function of its inputs, which is what its ~40
    /// snapshot tests depend on.
    /// </remarks>
    public IReadOnlyList<IPromptContributor>? PromptContributors { get; init; }
}

public static class SystemPromptBuilder
{
    private const string SilentReplyToken = "NO_REPLY";
    private const string SystemPromptCacheBoundary = "\n<!-- BOTNEXUS_CACHE_BOUNDARY -->\n";
    private const string MemoryPromptInjectionFull = "full";
    private const string MemoryPromptInjectionSummary = "summary";
    private const string MemoryPromptInjectionNone = "none";
    private const bool IncludeReplyTagsSectionByDefault = false;

    /// <summary>
    /// Declarative ordering for the prompt pipeline sections. Each value is the sort key passed to
    /// <see cref="LambdaPromptSection"/>; the section with the lowest key renders first. Naming the
    /// keys (rather than scattering bare int literals across the pipeline) makes the section order
    /// readable and keeps the gaps that leave room for future sections to slot in between.
    /// </summary>
    private static class PromptOrder
    {
        public const int Tooling = 10;
        public const int Safety = 40;
        public const int Cli = 42;
        public const int Memory = 60;
        public const int SelfUpdate = 70;
        public const int ModelAliases = 80;
        public const int Workspace = 90;
        public const int Docs = 100;
        public const int UserIdentity = 110;
        public const int Time = 120;
        public const int ConversationContext = 125;
        public const int ConversationInstructions = 127;
        public const int ConversationTodo = 128;
        public const int WorkspaceFilesHeader = 130;
        public const int ReplyTags = 140;
        public const int Conversations = 145;
        public const int Messaging = 150;
        public const int Canvas = 155;
        public const int Voice = 160;
        public const int Reasoning = 170;
        public const int StableProjectContext = 180;
        public const int SilentReplies = 190;
        public const int CacheBoundary = 200;
        public const int DynamicProjectContext = 210;
        public const int ExtraSystemPrompt = 220;
        public const int Heartbeat = 230;
        public const int Runtime = 240;
    }

        public static string Build(SystemPromptParams @params)
    {
        ArgumentNullException.ThrowIfNull(@params);

        if (@params.PromptMode == PromptMode.None)
            return "You are a personal assistant running inside BotNexus.";

        var toolRegistry = new ToolNameRegistry(@params.ToolNames);
        var rawToolNames = toolRegistry.RawTools;
        var normalizedTools = toolRegistry.NormalizedTools;
        var isMinimal = @params.PromptMode is PromptMode.Minimal;
        var hasGateway = normalizedTools.Contains("gateway");
        var hasCronTool = normalizedTools.Contains("cron") || rawToolNames.Count == 0;
        var hasUpdatePlanTool = normalizedTools.Contains("update_plan");
        var readToolName = toolRegistry.Resolve("read");
        var execToolName = toolRegistry.Resolve("exec");
        var processToolName = toolRegistry.Resolve("process");
        var runtimeChannel = @params.Runtime?.Channel?.Trim().ToLowerInvariant();
        var runtimeCapabilities = PromptText.NormalizeCapabilityIds(@params.Runtime?.Capabilities ?? []);
        var inlineButtonsEnabled = runtimeCapabilities.Contains("inlinebuttons", StringComparer.Ordinal);

        var contextFiles = (@params.ContextFiles ?? []).Where(static file => !string.IsNullOrWhiteSpace(file.Path)).ToList();
        var orderedContextFiles = SortContextFilesForPrompt(contextFiles);
        var stableContextFiles = orderedContextFiles.Where(static file => !IsDynamicContextFile(file.Path)).ToList();
        var dynamicContextFiles = orderedContextFiles.Where(static file => IsDynamicContextFile(file.Path)).ToList();

        var promptContext = new PromptContext
        {
            WorkspaceDir = @params.WorkspaceDir,
            ContextFiles = contextFiles.Select(static file => new BotNexus.Gateway.Prompts.ContextFile(file.Path, file.Content)).ToList(),
            AvailableTools = normalizedTools,
            IsMinimal = isMinimal,
            Channel = runtimeChannel,
            Extensions = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [GatewayPromptDataKey] = new GatewayPromptData(
                    @params,
                    rawToolNames,
                    normalizedTools,
                    hasGateway,
                    hasCronTool,
                    hasUpdatePlanTool,
                    readToolName,
                    execToolName,
                    processToolName,
                    runtimeChannel,
                    runtimeCapabilities,
                    inlineButtonsEnabled,
                    stableContextFiles,
                    dynamicContextFiles),
                [ModelGuidanceSection.ModelIdExtensionKey] = @params.Runtime?.Model,
                [ModelGuidanceSection.ProviderIdExtensionKey] = @params.Runtime?.Provider
            }
        };

        var pipeline = new PromptPipeline()
            .Add(new LambdaPromptSection(PromptOrder.Tooling, BuildToolingSection, xmlTag: "tooling"))
            .Add(ToolEnforcementSection.Create())
            .Add(ShellEfficiencySection.Create())
            .Add(new LambdaPromptSection(PromptOrder.Safety, BuildSafetySection, xmlTag: "safety"))
            .Add(new LambdaPromptSection(PromptOrder.Cli, BuildCliSection, xmlTag: "cli"))
            .Add(SkillsGuidanceSection.Create())
            .Add(SubAgentScopingSection.Create())
            .Add(new LambdaPromptSection(PromptOrder.Memory, BuildMemoryGuidanceSection, xmlTag: "memory"))
            .Add(new LambdaPromptSection(PromptOrder.SelfUpdate, BuildSelfUpdateSection, static context => GetGatewayData(context).HasGateway && !GetGatewayData(context).IsMinimal))
            .Add(new LambdaPromptSection(PromptOrder.ModelAliases, BuildModelAliasesSection))
            .Add(new LambdaPromptSection(PromptOrder.Workspace, BuildWorkspaceSection, xmlTag: "workspace"))
            .Add(new LambdaPromptSection(PromptOrder.Docs, BuildDocsGuidanceSection))
            .Add(new LambdaPromptSection(PromptOrder.UserIdentity, BuildUserIdentityGuidanceSection))
            .Add(new LambdaPromptSection(PromptOrder.Time, BuildTimeGuidanceSection))
            .Add(new LambdaPromptSection(PromptOrder.ConversationContext, BuildConversationContextSection, HasConversationContext))
            .Add(new LambdaPromptSection(PromptOrder.ConversationInstructions, BuildConversationInstructionsSection, HasConversationInstructions))
            .Add(new LambdaPromptSection(PromptOrder.ConversationTodo, BuildConversationTodoSection, HasConversationTodo, xmlTag: "conversation_todo"))
            .Add(new LambdaPromptSection(PromptOrder.WorkspaceFilesHeader, static _ => ["## Workspace Files (injected)", "These user-editable files are loaded by BotNexus and included below in Project Context.", string.Empty]))
            .Add(ModelGuidanceSection.Create())
            .Add(ModelAwarenessSection.Create())
            .Add(new LambdaPromptSection(PromptOrder.ReplyTags, BuildReplyTagsGuidanceSection, static _ => IncludeReplyTagsSectionByDefault))
            .Add(new LambdaPromptSection(PromptOrder.Conversations, BuildConversationsGuidanceSection, HasConversationTool, xmlTag: "conversations"))
            .Add(new LambdaPromptSection(PromptOrder.Messaging, BuildMessagingGuidanceSection, xmlTag: "messaging"))
            .Add(new LambdaPromptSection(PromptOrder.Canvas, BuildCanvasGuidanceSection, HasCanvasTool, xmlTag: "canvas"))
            .Add(new LambdaPromptSection(PromptOrder.Voice, BuildVoiceGuidanceSection))
            .Add(new LambdaPromptSection(PromptOrder.Reasoning, BuildReasoningSection, static context => GetGatewayData(context).Parameters.ReasoningTagHint))
            .Add(new LambdaPromptSection(PromptOrder.StableProjectContext, BuildStableProjectContextSection))
            .Add(new LambdaPromptSection(PromptOrder.SilentReplies, BuildSilentRepliesSection, static context => !GetGatewayData(context).IsMinimal, xmlTag: "silent_replies"))
            .Add(new LambdaPromptSection(PromptOrder.CacheBoundary, static _ => [SystemPromptCacheBoundary]))
            .Add(new LambdaPromptSection(PromptOrder.DynamicProjectContext, BuildDynamicProjectContextSection))
            .Add(new LambdaPromptSection(PromptOrder.ExtraSystemPrompt, BuildExtraSystemPromptSection))
            .Add(new LambdaPromptSection(PromptOrder.Heartbeat, BuildHeartbeatSection, static context => !GetGatewayData(context).IsMinimal))
            .Add(new LambdaPromptSection(PromptOrder.Runtime, BuildRuntimeSection, xmlTag: "runtime"));

        // #3667: the one call that makes IPromptContributor a real extension point. Contributors
        // are ordered against the PromptOrder keys above by their Priority (or the contribution's
        // own Order), so an extension can place its block between built-in sections. Passing an
        // empty collection is a no-op, which is why every existing caller renders unchanged.
        pipeline.AddContributors(@params.PromptContributors ?? []);

        var lines = pipeline.BuildLines(promptContext);
        return string.Join("\n", lines.Where(static line => !string.IsNullOrEmpty(line)));
    }
    public static IReadOnlyList<ContextFile> SortContextFilesForPrompt(IReadOnlyList<ContextFile> contextFiles)
    {
        return ContextFileOrdering.SortForPrompt(contextFiles.Select(static file => new BotNexus.Gateway.Prompts.ContextFile(file.Path, file.Content)).ToList())
            .Select(static file => new ContextFile(file.Path, file.Content))
            .ToList();
    }

    public static IReadOnlyList<string> BuildProjectContextSection(IReadOnlyList<ContextFile> files, string heading, bool dynamic)
    {
        if (files.Count == 0)
            return [];

        List<string> lines = [heading, ""];
        if (dynamic)
        {
            lines.Add("The following frequently-changing project context files are kept below the cache boundary when possible:");
            lines.Add(string.Empty);
        }
        else
        {
            var hasSoulFile = files.Any(file => string.Equals(GetContextFileBasename(file.Path), "soul.md", StringComparison.Ordinal));
            lines.Add("The following project context files have been loaded:");
            if (hasSoulFile)
                lines.Add("If SOUL.md is present, embody its persona and tone. Avoid stiff, generic replies; follow its guidance unless higher-priority instructions override it.");

            lines.Add(string.Empty);
        }

        foreach (var file in files)
        {
            lines.Add($"## {file.Path}");
            lines.Add(string.Empty);
            lines.Add(file.Content);
            lines.Add(string.Empty);
        }

        return lines;
    }


    public static IReadOnlyList<string> BuildMemorySection(bool isMinimal, IReadOnlySet<string> availableTools)
    {
        return BuildMemorySection(isMinimal, null, availableTools);
    }

    public static IReadOnlyList<string> BuildMemorySection(bool isMinimal, string? promptInjectionMode, IReadOnlySet<string> availableTools)
    {
        return BuildMemorySection(isMinimal, promptInjectionMode, availableTools, ConversationScope.Private);
    }

    /// <summary>
    /// Builds the memory-guidance block, or nothing at all in a shared conversation (issue #2846).
    /// </summary>
    /// <remarks>
    /// In a shared conversation the owner-private files this guidance describes are withheld from
    /// the prompt, so emitting the guidance would both describe absent context and instruct the
    /// agent to write durable owner memory from a conversation it does not privately own.
    /// </remarks>
    public static IReadOnlyList<string> BuildMemorySection(
        bool isMinimal,
        string? promptInjectionMode,
        IReadOnlySet<string> availableTools,
        ConversationScope scope)
    {
        if (isMinimal)
            return [];

        if (scope == ConversationScope.Shared)
            return [];

        var mode = NormalizeMemoryPromptInjection(promptInjectionMode);
        if (string.Equals(mode, MemoryPromptInjectionNone, StringComparison.Ordinal))
            return [];

        if (string.Equals(mode, MemoryPromptInjectionSummary, StringComparison.Ordinal))
        {
            return
            [
                "Memory context is a snapshot loaded at session start and does not auto-refresh during this turn.",
                BuildMemoryWriteGuidance(availableTools),
                "Durable memory writes become available in future sessions after persistence.",
                ""
            ];
        }

        return
        [
            "Memory context in this prompt is frozen at session start; do not assume memory files changed unless a new session starts.",
            BuildMemoryWriteGuidance(availableTools),
            "Use `MEMORY.md` as long-lived consolidated context and `memory/YYYY-MM-DD.md` as append-only daily notes.",
            "Do not rewrite prior memory notes in-place during normal turns; append durable updates instead.",
            "Durable memory writes appear in subsequent sessions after persistence and prompt rebuild.",
            ""
        ];
    }

    public static IReadOnlyList<string> BuildUserIdentitySection(string? ownerLine, bool isMinimal)
    {
        if (string.IsNullOrWhiteSpace(ownerLine) || isMinimal)
            return [];

        return ["## Authorized Senders", ownerLine.Trim(), ""];
    }

    public static IReadOnlyList<string> BuildTimeSection(string? userTimezone)
    {
        if (string.IsNullOrWhiteSpace(userTimezone))
            return [];

        return ["## Current Date & Time", $"Time zone: {userTimezone.Trim()}", ""];
    }

    public static IReadOnlyList<string> BuildReplyTagsSection(bool isMinimal)
    {
        if (isMinimal)
            return [];

        return
        [
            "## Reply Tags",
            "To request a native reply/quote on supported surfaces, include one tag in your reply:",
            "- Reply tags must be the very first token in the message (no leading text/newlines): [[reply_to_current]] your reply.",
            "- [[reply_to_current]] replies to the triggering message.",
            "- Prefer [[reply_to_current]]. Use [[reply_to:<id>]] only when an id was explicitly provided (e.g. by the user or a tool).",
            "Whitespace inside the tag is allowed (e.g. [[ reply_to_current ]] / [[ reply_to: 123 ]]).",
            "Tags are stripped before sending; support depends on the current channel config.",
            ""
        ];
    }

    /// <summary>
    /// Gate for the conversations capability section (#2938). Guidance about creating and querying
    /// conversations is only actionable for an agent that actually holds the <c>conversation</c>
    /// tool, and minimal prompts (sub-agents) deliberately carry no orchestration guidance.
    /// </summary>
    private static bool HasConversationTool(PromptContext context)
    {
        var data = GetGatewayData(context);
        return !data.IsMinimal && data.NormalizedTools.Contains("conversation");
    }

    private static IReadOnlyList<string> BuildConversationsGuidanceSection(PromptContext context)
    {
        var data = GetGatewayData(context);
        return BuildConversationsSection(data.IsMinimal, data.NormalizedTools);
    }

    /// <summary>
    /// Concurrent-conversation capability guidance (#2938). Agents were defaulting to a
    /// single-threaded, one-conversation-at-a-time mental model that does not match the platform:
    /// there is no per-agent or per-conversation turn lock anywhere in the gateway. That gap pushed
    /// agents toward <c>spawn_subagent</c> and cron jobs for parallelism they already had, and
    /// toward rebuilding overlap-detection state externally. This is platform truth, kept short on
    /// purpose - every line is paid for on every turn.
    /// </summary>
    public static IReadOnlyList<string> BuildConversationsSection(bool isMinimal, IReadOnlySet<string> availableTools)
    {
        ArgumentNullException.ThrowIfNull(availableTools);

        if (isMinimal || !availableTools.Contains("conversation"))
            return [];

        return
        [
            "- You can hold many conversations at once. Turns in different conversations are NOT serialised against each other - they run concurrently, so work split across conversations does not queue up.",
            "- You can start one yourself: `conversation new`, and an opening message with `speak_as: user` begins a turn there. No cron job and no sub-agent is needed just to start work.",
            "- `conversation list` (filter by `status`, read title and purpose) shows what you are already doing across your own conversations - use it to avoid duplicate work and to check coverage before starting something new.",
            "- Conversations are durable: they survive session restarts and context compaction, unlike the session-scoped sub-agent registry, which is blind across sessions.",
            "- Every one of your conversations is the SAME agent identity - you. Opening one and speaking into it is not a second agent, and never narrate that work as someone else's. `spawn_subagent` remains the separate-worker primitive.",
            string.Empty
        ];
    }

    public static IReadOnlyList<string> BuildMessagingSection(
        bool isMinimal,
        IReadOnlySet<string> availableTools,
        string? runtimeChannel,
        bool inlineButtonsEnabled)
    {
        if (isMinimal)
            return [];

        var lines = new List<string>
        {
            "- Reply in current session → automatically routes to the source channel (Signal, Telegram, etc.)",
            "- Cross-session messaging → use sessions_send(sessionKey, message)",
            "- Sub-agent orchestration → use subagents(action=list|steer|kill)",
            $"- Runtime-generated completion events may ask for a user update. Rewrite those in your normal assistant voice and send the update (do not forward raw internal metadata or default to {SilentReplyToken}).",
            "- Never use exec/curl for provider messaging; BotNexus handles all routing internally."
        };

        if (availableTools.Contains("conversation"))
        {
            // #2938: this list previously routed only to sessions_send and subagents, i.e. away from
            // conversations, reinforcing the single-conversation model the <conversations> section
            // corrects. Insert the cross-reference next to the other routing choices.
            lines.Insert(2, "- Another of your own conversations → use the `conversation` tool (`new` to start one, `message` to speak into it, `list` to see them); see <conversations>.");
        }

        if (availableTools.Contains("message"))
        {
            lines.Add(string.Empty);
            lines.Add("### message tool");
            lines.Add("- Use `message` for proactive sends + channel actions (polls, reactions, etc.).");
            lines.Add("- For `action=send`, include `to` and `message`.");
            lines.Add("- If multiple channels are configured, pass `channel` (discord|signal|slack|telegram|webchat).");
            lines.Add($"- If you use `message` (`action=send`) to deliver your user-visible reply, respond with ONLY: {SilentReplyToken} (avoid duplicate replies).");
            lines.Add(inlineButtonsEnabled
                ? "- Inline buttons supported. Use `action=send` with `buttons=[[{text,callback_data,style?}]]`; `style` can be `primary`, `success`, or `danger`."
                : !string.IsNullOrWhiteSpace(runtimeChannel)
                    ? $"- Inline buttons not enabled for {runtimeChannel}. If you need them, ask to set {runtimeChannel}.capabilities.inlineButtons (\"dm\"|\"group\"|\"all\"|\"allowlist\")."
                    : string.Empty);
        }

        lines.Add(string.Empty);
        return lines.Where(static line => !string.IsNullOrWhiteSpace(line)).ToList();
    }

    public static IReadOnlyList<string> BuildVoiceSection(bool isMinimal, string? ttsHint)
    {
        if (isMinimal)
            return [];

        var hint = NormalizeStructuredPromptSection(ttsHint);
        if (string.IsNullOrWhiteSpace(hint))
            return [];

        return ["## Voice (TTS)", hint, ""];
    }

    /// <summary>
    /// Canvas guidance (#2974). This deliberately says what the canvas is FOR and when a file beats
    /// it; the mechanics (render/clear/state/submitToAgent) already live in the tool description and
    /// restating them here would be paid for on every single turn. It is written as a two-sided
    /// trigger list on purpose: unconditional "prefer the canvas" encouragement produces canvas
    /// renders for two-line answers, which is a worse outcome than the canvas going unused.
    /// </summary>
    public static IReadOnlyList<string> BuildCanvasSection(bool isMinimal, IReadOnlySet<string> availableTools)
    {
        ArgumentNullException.ThrowIfNull(availableTools);

        if (isMinimal || !availableTools.Contains("canvas"))
            return [];

        return
        [
            "The canvas is a rendered HTML panel in the portal, alongside the conversation. It is for output the user LOOKS AT rather than processes.",
            "Use it for: tabular or comparative data (especially sortable/filterable), anything graphable (trends, distributions, dependency or flow diagrams), and forms or choices the user hands back to you.",
            "Do NOT use the canvas for: short prose answers; content the user will grep, diff, or feed to another tool -- a file is the right surface for that; or anything that must outlive the conversation, because canvas state is per-conversation.",
            "The canvas is only visible in the portal. On other channels the reply must still carry the answer -- never respond with only a pointer to a render.",
            ""
        ];
    }

    public static IReadOnlyList<string> BuildDocsSection(string? docsPath, bool isMinimal, string readToolName)
    {
        _ = readToolName;
        var normalizedDocsPath = NormalizeStructuredPromptSection(docsPath);
        if (string.IsNullOrWhiteSpace(normalizedDocsPath) || isMinimal)
            return [];

        return
        [
            "## Documentation",
            $"BotNexus docs: {normalizedDocsPath}",
            "Mirror: https://docs.botnexus.ai",
            "Source: https://github.com/botnexus/botnexus",
            "Community: https://discord.com/invite/clawd",
            "Find new skills: https://clawhub.ai",
            "For BotNexus behavior, commands, config, or architecture: consult local docs first.",
            "When diagnosing issues, run `botnexus status` yourself when possible; only ask the user if you lack access (e.g., sandboxed).",
            ""
        ];
    }

    public static string BuildRuntimeLine(RuntimeInfo? runtime)
    {
        return RuntimeLineFormatter.BuildRuntimeLine(runtime is null ? null : new PromptRuntimeInfo
        {
            AgentId = runtime.AgentId,
            Host = runtime.Host,
            Os = runtime.Os,
            Arch = runtime.Arch,
            Provider = runtime.Provider,
            ContextWindow = runtime.ContextWindow,
            Model = runtime.Model,
            DefaultModel = runtime.DefaultModel,
            Shell = runtime.Shell,
            Channel = runtime.Channel,
            ClientKind = runtime.ClientKind,
            Capabilities = runtime.Capabilities,
            SessionId = runtime.SessionId,
            SessionKey = runtime.SessionKey
        });
    }

    public static IReadOnlyList<string> BuildOverridablePromptSection(string? overrideValue, IReadOnlyList<string> fallback)
    {
        var overrideSection = NormalizeStructuredPromptSection(overrideValue);
        if (!string.IsNullOrWhiteSpace(overrideSection))
            return [overrideSection, ""];

        return fallback;
    }

    private const string GatewayPromptDataKey = "gateway";

    private static GatewayPromptData GetGatewayData(PromptContext context)
        => context.Get<GatewayPromptData>(GatewayPromptDataKey)
            ?? throw new InvalidOperationException("Gateway prompt context data is missing.");

    // The following adapters bridge the pipeline's PromptContext to the primitive-argument section
    // builders. Each hoists GetGatewayData(context) once (rather than re-resolving it per argument)
    // so they are consistent in shape with the extracted Build*Section methods below.

    private static IReadOnlyList<string> BuildMemoryGuidanceSection(PromptContext context)
    {
        var data = GetGatewayData(context);
        return BuildMemorySection(data.IsMinimal, data.Parameters.MemoryPromptInjection, data.NormalizedTools, data.Parameters.Scope);
    }

    private static IReadOnlyList<string> BuildDocsGuidanceSection(PromptContext context)
    {
        var data = GetGatewayData(context);
        return BuildDocsSection(data.Parameters.DocsPath, data.IsMinimal, data.ReadToolName);
    }

    private static IReadOnlyList<string> BuildUserIdentityGuidanceSection(PromptContext context)
    {
        var data = GetGatewayData(context);
        return BuildUserIdentitySection(data.Parameters.OwnerIdentity, data.IsMinimal);
    }

    private static IReadOnlyList<string> BuildTimeGuidanceSection(PromptContext context)
    {
        var data = GetGatewayData(context);
        return BuildTimeSection(data.Parameters.UserTimezone);
    }

    private static IReadOnlyList<string> BuildReplyTagsGuidanceSection(PromptContext context)
    {
        var data = GetGatewayData(context);
        return BuildReplyTagsSection(data.IsMinimal);
    }

    private static IReadOnlyList<string> BuildMessagingGuidanceSection(PromptContext context)
    {
        var data = GetGatewayData(context);
        return BuildMessagingSection(data.IsMinimal, data.NormalizedTools, data.RuntimeChannel, data.InlineButtonsEnabled);
    }

    private static IReadOnlyList<string> BuildVoiceGuidanceSection(PromptContext context)
    {
        var data = GetGatewayData(context);
        return BuildVoiceSection(data.IsMinimal, data.Parameters.TtsHint);
    }

    /// <summary>
    /// Gate for the canvas guidance section. The canvas is a real output surface that agents were
    /// simply never told about (#2974), but guidance an agent cannot act on is pure token cost, so
    /// the section is emitted only when the agent actually holds the <c>canvas</c> tool.
    /// </summary>
    private static bool HasCanvasTool(PromptContext context)
    {
        var data = GetGatewayData(context);
        return !data.IsMinimal && data.NormalizedTools.Contains("canvas");
    }

    private static IReadOnlyList<string> BuildCanvasGuidanceSection(PromptContext context)
    {
        var data = GetGatewayData(context);
        return BuildCanvasSection(data.IsMinimal, data.NormalizedTools);
    }

    private static IReadOnlyList<string> BuildStableProjectContextSection(PromptContext context)
    {
        var data = GetGatewayData(context);
        return BuildProjectContextSection(data.StableContextFiles, "# Project Context", dynamic: false);
    }

    private static IReadOnlyList<string> BuildDynamicProjectContextSection(PromptContext context)
    {
        var data = GetGatewayData(context);
        return BuildProjectContextSection(
            data.DynamicContextFiles,
            data.StableContextFiles.Count > 0 ? "# Dynamic Project Context" : "# Project Context",
            dynamic: true);
    }

    private static IReadOnlyList<string> BuildToolingSection(PromptContext context)
    {
        var data = GetGatewayData(context);
        var lines = new List<string>
        {
            "You are a personal assistant running inside BotNexus.",
            string.Empty,
            "Structured tool definitions are the source of truth for tool names, descriptions, and parameters.",
            "Tool names are case-sensitive. Call tools exactly as listed in the structured tool definitions.",
            "If a tool is present in the structured tool definitions, it is available unless a later tool call reports a policy/runtime restriction.",
            "TOOLS.md does not control tool availability; it is user guidance for how to use external tools."
        };

        lines.AddRange(data.HasCronTool
            ? [
                $"For follow-up at a future time (for example \"check back in 10 minutes\", reminders, run-later work, or recurring tasks), use cron instead of {data.ExecToolName} sleep, yieldMs delays, or {data.ProcessToolName} polling.",
                $"Use {data.ExecToolName}/{data.ProcessToolName} only for commands that start now and continue running in the background.",
                $"For long-running work that starts now, start it once and rely on automatic completion wake when it is enabled and the command emits output or fails; otherwise use {data.ProcessToolName} to confirm completion, and use it for logs, status, input, or intervention.",
                "Do not emulate scheduling with sleep loops, timeout loops, or repeated polling."
            ]
            : [
                $"For long waits, avoid rapid poll loops: use {data.ExecToolName} with enough yieldMs or {data.ProcessToolName}(action=poll, timeout=<ms>).",
                $"For long-running work that starts now, start it once and rely on automatic completion wake when it is enabled and the command emits output or fails; otherwise use {data.ProcessToolName} to confirm completion, and use it for logs, status, input, or intervention."
            ]);

        if (data.HasUpdatePlanTool)
        {
            lines.AddRange(
            [
                "For non-trivial multi-step work, keep a short plan updated with `update_plan`.",
                "Skip `update_plan` for simple tasks, obvious one-step fixes, or work you can finish in a few direct actions.",
                "When you use `update_plan`, keep exactly one step `in_progress` until the work is done.",
                "After calling `update_plan`, continue the work and do not repeat the full plan unless the user asks."
            ]);
        }

        lines.Add("If a task is more complex or takes longer, spawn a sub-agent. Completion is push-based: it will auto-announce when done.");
        lines.Add("Do not poll `subagents list` / `sessions_list` in a loop; only check status on-demand (for intervention, debugging, or when explicitly asked).");
        lines.Add(string.Empty);
        lines.Add(BuildExecApprovalPromptGuidance(data.RuntimeChannel, data.InlineButtonsEnabled));
        lines.Add("Never execute /approve through exec or any other shell/tool path; /approve is a user-facing approval command, not a shell command.");
        lines.Add("Treat allow-once as single-command only: if another elevated command needs approval, request a fresh /approve and do not claim prior approval covered it.");
        lines.Add("When approvals are required, preserve and show the full command/script exactly as provided (including chained operators like &&, ||, |, ;, or multiline shells) so the user can approve what will actually run.");
        return lines;
    }

    private static IReadOnlyList<string> BuildSafetySection(PromptContext _)
    {
        return
        [
            "You have no independent goals: do not pursue self-preservation, replication, resource acquisition, or power-seeking; avoid long-term plans beyond the user's request.",
            "Prioritize safety and human oversight over completion; if instructions conflict, pause and ask; comply with stop/pause/audit requests and never bypass safeguards. (Inspired by Anthropic's constitution.)",
            "Do not manipulate or persuade anyone to expand access or disable safeguards. Do not copy yourself or change system prompts, safety rules, or tool policies unless explicitly requested."
        ];
    }

    private static IReadOnlyList<string> BuildCliSection(PromptContext _)
    {
        return
        [
            "BotNexus is controlled via subcommands. Do not invent commands.",
            "To manage the Gateway daemon service (start/stop/restart):",
            "- botnexus gateway status",
            "- botnexus gateway start",
            "- botnexus gateway stop",
            "- botnexus gateway restart",
            "If unsure, ask the user to run `botnexus help` (or `botnexus gateway --help`) and paste the output."
        ];
    }

    private static IReadOnlyList<string> BuildSelfUpdateSection(PromptContext context)
    {
        _ = context;
        return
        [
            "## BotNexus Self-Update",
            "Get Updates (self-update) is ONLY allowed when the user explicitly asks for it.",
            "Do not run config.apply or update.run unless the user explicitly requests an update or config change; if it's not explicit, ask first.",
            "Use config.schema.lookup with a specific dot path to inspect only the relevant config subtree before making config changes or answering config-field questions; avoid guessing field names/types.",
            "Actions: config.schema.lookup, config.get, config.apply (validate + write full config, then restart), config.patch (partial update, merges with existing), update.run (update deps or git, then restart).",
            "After restart, BotNexus pings the last active session automatically.",
            ""
        ];
    }

    private static IReadOnlyList<string> BuildModelAliasesSection(PromptContext context)
    {
        var data = GetGatewayData(context);
        if (data.Parameters.ModelAliasLines is not { Count: > 0 } || data.IsMinimal)
        {
            return [string.Empty];
        }

        var lines = new List<string>
        {
            string.Empty,
            "## Model Aliases",
            "Prefer aliases when specifying model overrides; full provider/model is also accepted."
        };
        lines.AddRange(data.Parameters.ModelAliasLines.Select(NormalizeStructuredPromptSection).Where(static line => !string.IsNullOrWhiteSpace(line)));
        lines.Add(string.Empty);
        return lines;
    }

    private static IReadOnlyList<string> BuildWorkspaceSection(PromptContext context)
    {
        var data = GetGatewayData(context);
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(data.Parameters.UserTimezone))
            lines.Add("If you need the current date, time, or day of week, run session_status (📊 session_status).");

        lines.Add($"Your working directory is: {data.Parameters.WorkspaceDir}");
        lines.Add("Treat this directory as the single global workspace for file operations unless explicitly instructed otherwise.");
        lines.AddRange((data.Parameters.WorkspaceNotes ?? []).Select(NormalizeStructuredPromptSection).Where(static line => !string.IsNullOrWhiteSpace(line)));
        lines.Add(string.Empty);
        return lines;
    }

    private static IReadOnlyList<string> BuildReasoningSection(PromptContext _)
        => ["## Reasoning Format", BuildReasoningHint(), string.Empty];

    private static IReadOnlyList<string> BuildSilentRepliesSection(PromptContext _)
        =>
        [
            $"Use {SilentReplyToken} ONLY when no user-visible reply is required.",
            "",
            "⚠️ Rules:",
            "- Valid cases: silent housekeeping, deliberate no-op ambient wakeups, or after a messaging tool already delivered the user-visible reply.",
            "- Never use it to avoid doing requested work or to end an actionable turn early.",
            "- It must be your ENTIRE message - nothing else",
            $"- Never append it to an actual response (never include \"{SilentReplyToken}\" in real replies)",
            "- Never wrap it in markdown or code blocks",
            "",
            $"❌ Wrong: \"Here's help... {SilentReplyToken}\"",
            $"❌ Wrong: \"{SilentReplyToken}\"",
            $"✅ Right: {SilentReplyToken}",
            ""
        ];

    private static IReadOnlyList<string> BuildExtraSystemPromptSection(PromptContext context)
    {
        var data = GetGatewayData(context);
        var extraSystemPrompt = NormalizeStructuredPromptSection(data.Parameters.ExtraSystemPrompt);
        if (string.IsNullOrWhiteSpace(extraSystemPrompt))
        {
            return [];
        }

        return
        [
            data.Parameters.PromptMode == PromptMode.Minimal ? "## Subagent Context" : "## Group Chat Context",
            extraSystemPrompt,
            string.Empty
        ];
    }

    private static IReadOnlyList<string> BuildHeartbeatSection(PromptContext context)
    {
        var data = GetGatewayData(context);
        var heartbeatPrompt = NormalizeStructuredPromptSection(data.Parameters.HeartbeatPrompt);
        if (string.IsNullOrWhiteSpace(heartbeatPrompt))
        {
            return [];
        }

        return
        [
            "## Heartbeats",
            $"Heartbeat prompt: {heartbeatPrompt}",
            "If you receive a heartbeat poll (a user message matching the heartbeat prompt above), and there is nothing that needs attention, reply exactly:",
            "HEARTBEAT_OK",
            "BotNexus treats a leading/trailing \"HEARTBEAT_OK\" as a heartbeat ack (and may discard it).",
            "If something needs attention, do NOT include \"HEARTBEAT_OK\"; reply with the alert text instead.",
            ""
        ];
    }

    private static IReadOnlyList<string> BuildRuntimeSection(PromptContext context)
    {
        var data = GetGatewayData(context);
        List<string> lines =
        [
            RuntimeLineFormatter.RuntimeContextBeginDelimiter,
            BuildRuntimeLine(data.Parameters.Runtime)
        ];

        // #2874: the old wording described an off|on|stream reasoning DISPLAY mode that no code
        // implements - the client's thinking visibility is a bool defaulting to true applied as a
        // CSS filter, and /reasoning is a per-conversation thinking-LEVEL override, not a toggle.
        // Report only the resolved level, and omit the subject entirely when it is unresolvable.
        var reasoningLevel = data.Parameters.ReasoningLevel;
        if (!string.IsNullOrWhiteSpace(reasoningLevel) &&
            !string.Equals(reasoningLevel, "off", StringComparison.OrdinalIgnoreCase))
        {
            lines.Add($"Reasoning: thinking level {reasoningLevel.Trim()} (per-conversation override: /reasoning <minimal|low|medium|high|xhigh|max> or /reasoning clear).");
        }

        lines.Add(RuntimeLineFormatter.RuntimeContextEndDelimiter);
        return lines;
    }

    private static bool HasConversationContext(PromptContext context)
    {
        var conversationContext = GetGatewayData(context).Parameters.ConversationContext;
        return conversationContext is not null &&
               (!string.IsNullOrWhiteSpace(conversationContext.Purpose) ||
                !string.Equals(conversationContext.Title, "New conversation", StringComparison.Ordinal));
    }

    private static IReadOnlyList<string> BuildConversationContextSection(PromptContext context)
    {
        var conversationContext = GetGatewayData(context).Parameters.ConversationContext
            ?? throw new InvalidOperationException("Conversation context is required.");

        List<string> lines =
        [
            "## Conversation Context",
            $"- **ID**: {conversationContext.ConversationId}",
            $"- **Title**: {conversationContext.Title}"
        ];

        if (!string.IsNullOrWhiteSpace(conversationContext.Purpose))
            lines.Add($"- **Purpose**: {conversationContext.Purpose}");

        return lines;
    }

    private static bool HasConversationInstructions(PromptContext context)
    {
        var conversationContext = GetGatewayData(context).Parameters.ConversationContext;
        return conversationContext is not null &&
               !string.IsNullOrWhiteSpace(conversationContext.Instructions);
    }

    private static IReadOnlyList<string> BuildConversationInstructionsSection(PromptContext context)
    {
        var conversationContext = GetGatewayData(context).Parameters.ConversationContext
            ?? throw new InvalidOperationException("ConversationContext is required for BuildConversationInstructionsSection.");

        return
        [
            "## Conversation Instructions",
            conversationContext.Instructions!
        ];
    }

    private static bool HasConversationTodo(PromptContext context)
    {
        var conversationContext = GetGatewayData(context).Parameters.ConversationContext;
        return conversationContext is not null
               && TodoPromptFormatter.BuildSection(conversationContext.Todo, conversationContext.RunStartedAt).Count > 0;
    }

    private static IReadOnlyList<string> BuildConversationTodoSection(PromptContext context)
    {
        var conversationContext = GetGatewayData(context).Parameters.ConversationContext
            ?? throw new InvalidOperationException("ConversationContext is required for BuildConversationTodoSection.");

        // #2984: RunStartedAt (cron only) splits this run's agenda from earlier runs' minutes. The
        // gate above must use the SAME arguments, or a section could be admitted and then render empty.
        return TodoPromptFormatter.BuildSection(conversationContext.Todo, conversationContext.RunStartedAt);
    }

    private sealed class LambdaPromptSection(
        int order,
        Func<PromptContext, IReadOnlyList<string>> build,
        Func<PromptContext, bool>? shouldInclude = null,
        string? xmlTag = null) : IPromptSection
    {
        public int Order => order;

        public string? XmlTag => xmlTag;

        public bool ShouldInclude(PromptContext context) => shouldInclude?.Invoke(context) ?? true;

        public IReadOnlyList<string> Build(PromptContext context) => build(context);
    }

    private sealed record GatewayPromptData(
        SystemPromptParams Parameters,
        IReadOnlyList<string> RawToolNames,
        IReadOnlySet<string> NormalizedTools,
        bool HasGateway,
        bool HasCronTool,
        bool HasUpdatePlanTool,
        string ReadToolName,
        string ExecToolName,
        string ProcessToolName,
        string? RuntimeChannel,
        IReadOnlyList<string> RuntimeCapabilities,
        bool InlineButtonsEnabled,
        IReadOnlyList<ContextFile> StableContextFiles,
        IReadOnlyList<ContextFile> DynamicContextFiles)
    {
        public bool IsMinimal => Parameters.PromptMode is PromptMode.Minimal;
    }

    private static string BuildExecApprovalPromptGuidance(string? runtimeChannel, bool inlineButtonsEnabled)
    {
        var usesNativeApprovalUi = string.Equals(runtimeChannel, "webchat", StringComparison.OrdinalIgnoreCase)
            || inlineButtonsEnabled;
        return usesNativeApprovalUi
            ? "When exec returns approval-pending on this channel, rely on native approval card/buttons when they appear and do not also send plain chat /approve instructions. Only include the concrete /approve command if the tool result says chat approvals are unavailable or only manual approval is possible."
            : "When exec returns approval-pending, include the concrete /approve command from tool output as plain chat text for the user, and do not ask for a different or rotated code.";
    }

    private static string BuildReasoningHint()
    {
        return string.Join(" ", new[]
        {
            "ALL internal reasoning MUST be inside <think>...</think>.",
            "Do not output any analysis outside <think>.",
            "Format every reply as <think>...</think> then <final>...</final>, with no other text.",
            "Only the final user-visible reply may appear inside <final>.",
            "Only text inside <final> is shown to the user; everything else is discarded and never seen by the user.",
            "Example:",
            "<think>Short internal reasoning.</think>",
            "<final>Hey there! What would you like to do next?</final>"
        });
    }

    private static string NormalizeStructuredPromptSection(string? value)
        => PromptText.NormalizeStructuredSection(value);

    private static IReadOnlyList<string> NormalizePromptCapabilityIds(IEnumerable<string> capabilities)
        => PromptText.NormalizeCapabilityIds(capabilities);

    private static string NormalizeMemoryPromptInjection(string? promptInjectionMode)
    {
        if (string.IsNullOrWhiteSpace(promptInjectionMode))
            return MemoryPromptInjectionFull;

        var normalized = promptInjectionMode.Trim().ToLowerInvariant();
        return normalized is MemoryPromptInjectionSummary or MemoryPromptInjectionNone
            ? normalized
            : MemoryPromptInjectionFull;
    }

    private static string BuildMemoryWriteGuidance(IReadOnlySet<string> availableTools) =>
        availableTools.Contains("memory_save")
            ? "Use `memory_save` for durable memory writes."
            : "Use the runtime's memory-write capability for durable memory writes when available.";

    private static bool IsDynamicContextFile(string pathValue) =>
        ContextFileOrdering.IsDynamic(pathValue);

    private static string NormalizeContextFilePath(string pathValue) =>
        ContextFileOrdering.NormalizePath(pathValue);

    private static string GetContextFileBasename(string pathValue)
        => ContextFileOrdering.GetBasename(pathValue);
}
