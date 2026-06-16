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
    public string? Shell { get; init; }
    public string? Channel { get; init; }
    public IReadOnlyList<string>? Capabilities { get; init; }
    public string? SessionId { get; init; }
    public string? SessionKey { get; init; }
}

public sealed record ConversationContext(string ConversationId, string Title, string? Purpose, string? Instructions = null, string? Todo = null);

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
    public ConversationContext? ConversationContext { get; init; }
}

public static class SystemPromptBuilder
{
    private const string SilentReplyToken = "NO_REPLY";
    private const string SystemPromptCacheBoundary = "\n<!-- BOTNEXUS_CACHE_BOUNDARY -->\n";
    private const string MemoryPromptInjectionFull = "full";
    private const string MemoryPromptInjectionSummary = "summary";
    private const string MemoryPromptInjectionNone = "none";
    private const bool IncludeReplyTagsSectionByDefault = false;

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
        var orderedContextFiles = sortContextFilesForPrompt(contextFiles);
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
                [ModelGuidanceSection.ModelIdExtensionKey] = @params.Runtime?.Model
            }
        };

        var pipeline = new PromptPipeline()
            .Add(new LambdaPromptSection(10, BuildToolingSection, xmlTag: "tooling"))
            .Add(ToolEnforcementSection.Create())
            .Add(ShellEfficiencySection.Create())
            .Add(new LambdaPromptSection(40, BuildSafetySection, xmlTag: "safety"))
            .Add(new LambdaPromptSection(42, BuildCliSection, xmlTag: "cli"))
            .Add(SkillsGuidanceSection.Create())
            .Add(new LambdaPromptSection(60, static context => buildMemorySection(
                GetGatewayData(context).IsMinimal,
                GetGatewayData(context).Parameters.MemoryPromptInjection,
                GetGatewayData(context).NormalizedTools), xmlTag: "memory"))
            .Add(new LambdaPromptSection(70, BuildSelfUpdateSection, static context => GetGatewayData(context).HasGateway && !GetGatewayData(context).IsMinimal))
            .Add(new LambdaPromptSection(80, BuildModelAliasesSection))
            .Add(new LambdaPromptSection(90, BuildWorkspaceSection, xmlTag: "workspace"))
            .Add(new LambdaPromptSection(100, static context => buildDocsSection(GetGatewayData(context).Parameters.DocsPath, GetGatewayData(context).IsMinimal, GetGatewayData(context).ReadToolName)))
            .Add(new LambdaPromptSection(110, static context => buildUserIdentitySection(GetGatewayData(context).Parameters.OwnerIdentity, GetGatewayData(context).IsMinimal)))
            .Add(new LambdaPromptSection(120, static context => buildTimeSection(GetGatewayData(context).Parameters.UserTimezone)))
            .Add(new LambdaPromptSection(125, BuildConversationContextSection, HasConversationContext))
            .Add(new LambdaPromptSection(127, BuildConversationInstructionsSection, HasConversationInstructions))
            .Add(new LambdaPromptSection(128, BuildConversationTodoSection, HasConversationTodo, xmlTag: "conversation_todo"))
            .Add(new LambdaPromptSection(130, static _ => ["## Workspace Files (injected)", "These user-editable files are loaded by BotNexus and included below in Project Context.", string.Empty]))
            .Add(ModelGuidanceSection.Create())
            .Add(new LambdaPromptSection(140, static context => buildReplyTagsSection(GetGatewayData(context).IsMinimal), static _ => IncludeReplyTagsSectionByDefault))
            .Add(new LambdaPromptSection(150, static context => buildMessagingSection(GetGatewayData(context).IsMinimal, GetGatewayData(context).NormalizedTools, GetGatewayData(context).RuntimeChannel, GetGatewayData(context).InlineButtonsEnabled), xmlTag: "messaging"))
            .Add(new LambdaPromptSection(160, static context => buildVoiceSection(GetGatewayData(context).IsMinimal, GetGatewayData(context).Parameters.TtsHint)))
            .Add(new LambdaPromptSection(170, BuildReasoningSection, static context => GetGatewayData(context).Parameters.ReasoningTagHint))
            .Add(new LambdaPromptSection(180, static context => buildProjectContextSection(GetGatewayData(context).StableContextFiles, "# Project Context", dynamic: false)))
            .Add(new LambdaPromptSection(190, BuildSilentRepliesSection, static context => !GetGatewayData(context).IsMinimal, xmlTag: "silent_replies"))
            .Add(new LambdaPromptSection(200, static _ => [SystemPromptCacheBoundary]))
            .Add(new LambdaPromptSection(210, static context => buildProjectContextSection(
                GetGatewayData(context).DynamicContextFiles,
                GetGatewayData(context).StableContextFiles.Count > 0 ? "# Dynamic Project Context" : "# Project Context",
                dynamic: true)))
            .Add(new LambdaPromptSection(220, BuildExtraSystemPromptSection))
            .Add(new LambdaPromptSection(230, BuildHeartbeatSection, static context => !GetGatewayData(context).IsMinimal))
            .Add(new LambdaPromptSection(240, BuildRuntimeSection, xmlTag: "runtime"));

        var lines = pipeline.BuildLines(promptContext);
        return string.Join("\n", lines.Where(static line => !string.IsNullOrEmpty(line)));
    }
    public static IReadOnlyList<ContextFile> sortContextFilesForPrompt(IReadOnlyList<ContextFile> contextFiles)
    {
        return ContextFileOrdering.SortForPrompt(contextFiles.Select(static file => new BotNexus.Gateway.Prompts.ContextFile(file.Path, file.Content)).ToList())
            .Select(static file => new ContextFile(file.Path, file.Content))
            .ToList();
    }

    public static IReadOnlyList<string> buildProjectContextSection(IReadOnlyList<ContextFile> files, string heading, bool dynamic)
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


    public static IReadOnlyList<string> buildMemorySection(bool isMinimal, IReadOnlySet<string> availableTools)
    {
        return buildMemorySection(isMinimal, null, availableTools);
    }

    public static IReadOnlyList<string> buildMemorySection(bool isMinimal, string? promptInjectionMode, IReadOnlySet<string> availableTools)
    {
        if (isMinimal)
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

    public static IReadOnlyList<string> buildUserIdentitySection(string? ownerLine, bool isMinimal)
    {
        if (string.IsNullOrWhiteSpace(ownerLine) || isMinimal)
            return [];

        return ["## Authorized Senders", ownerLine.Trim(), ""];
    }

    public static IReadOnlyList<string> buildTimeSection(string? userTimezone)
    {
        if (string.IsNullOrWhiteSpace(userTimezone))
            return [];

        return ["## Current Date & Time", $"Time zone: {userTimezone.Trim()}", ""];
    }

    public static IReadOnlyList<string> buildReplyTagsSection(bool isMinimal)
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

    public static IReadOnlyList<string> buildMessagingSection(
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

    public static IReadOnlyList<string> buildVoiceSection(bool isMinimal, string? ttsHint)
    {
        if (isMinimal)
            return [];

        var hint = NormalizeStructuredPromptSection(ttsHint);
        if (string.IsNullOrWhiteSpace(hint))
            return [];

        return ["## Voice (TTS)", hint, ""];
    }

    public static IReadOnlyList<string> buildDocsSection(string? docsPath, bool isMinimal, string readToolName)
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

    public static string buildRuntimeLine(RuntimeInfo? runtime)
    {
        return RuntimeLineFormatter.BuildRuntimeLine(runtime is null ? null : new PromptRuntimeInfo
        {
            AgentId = runtime.AgentId,
            Host = runtime.Host,
            Os = runtime.Os,
            Arch = runtime.Arch,
            Provider = runtime.Provider,
            Model = runtime.Model,
            DefaultModel = runtime.DefaultModel,
            Shell = runtime.Shell,
            Channel = runtime.Channel,
            Capabilities = runtime.Capabilities,
            SessionId = runtime.SessionId,
            SessionKey = runtime.SessionKey
        });
    }

    public static IReadOnlyList<string> buildOverridablePromptSection(string? overrideValue, IReadOnlyList<string> fallback)
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
        lines.Add(buildExecApprovalPromptGuidance(data.RuntimeChannel, data.InlineButtonsEnabled));
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
        return
        [
            RuntimeLineFormatter.RuntimeContextBeginDelimiter,
            buildRuntimeLine(data.Parameters.Runtime),
            $"Reasoning: {(data.Parameters.ReasoningLevel ?? "off")} (hidden unless on/stream). Toggle /reasoning; /status shows Reasoning when enabled.",
            RuntimeLineFormatter.RuntimeContextEndDelimiter
        ];
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
               && TodoPromptFormatter.BuildSection(conversationContext.Todo).Count > 0;
    }

    private static IReadOnlyList<string> BuildConversationTodoSection(PromptContext context)
    {
        var conversationContext = GetGatewayData(context).Parameters.ConversationContext
            ?? throw new InvalidOperationException("ConversationContext is required for BuildConversationTodoSection.");

        return TodoPromptFormatter.BuildSection(conversationContext.Todo);
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

    private static string buildExecApprovalPromptGuidance(string? runtimeChannel, bool inlineButtonsEnabled)
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
