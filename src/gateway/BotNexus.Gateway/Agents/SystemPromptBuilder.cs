using BotNexus.Prompts;

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
}

public sealed record SystemPromptParams
{
    public required string WorkspaceDir { get; init; }
    public string? ExtraSystemPrompt { get; init; }
    public IReadOnlyList<string>? ToolNames { get; init; }
    public string? UserTimezone { get; init; }
    public IReadOnlyList<ContextFile>? ContextFiles { get; init; }
    public string? SkillsPrompt { get; init; }
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
}

public static class SystemPromptBuilder
{
    private const string SilentReplyToken = "NO_REPLY";
    private const string SystemPromptCacheBoundary = "\n<!-- BOTNEXUS_CACHE_BOUNDARY -->\n";

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
            ContextFiles = contextFiles.Select(static file => new BotNexus.Prompts.ContextFile(file.Path, file.Content)).ToList(),
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
                    dynamicContextFiles)
            }
        };

        var pipeline = new PromptPipeline()
            .Add(new LambdaPromptSection(10, BuildToolingSection))
            .Add(new LambdaPromptSection(20, BuildToolCallStyleSection))
            .Add(new LambdaPromptSection(30, static context => buildOverridablePromptSection(null, buildExecutionBiasSection(GetGatewayData(context).IsMinimal))))
            .Add(new LambdaPromptSection(40, BuildSafetyAndCliSection))
            .Add(new LambdaPromptSection(50, static context => buildSkillsSection(GetGatewayData(context).Parameters.SkillsPrompt, GetGatewayData(context).ReadToolName)))
            .Add(new LambdaPromptSection(60, static context => buildMemorySection(GetGatewayData(context).IsMinimal, GetGatewayData(context).NormalizedTools)))
            .Add(new LambdaPromptSection(70, BuildSelfUpdateSection, static context => GetGatewayData(context).HasGateway && !GetGatewayData(context).IsMinimal))
            .Add(new LambdaPromptSection(80, BuildModelAliasesSection))
            .Add(new LambdaPromptSection(90, BuildWorkspaceSection))
            .Add(new LambdaPromptSection(100, static context => buildDocsSection(GetGatewayData(context).Parameters.DocsPath, GetGatewayData(context).IsMinimal, GetGatewayData(context).ReadToolName)))
            .Add(new LambdaPromptSection(110, static context => buildUserIdentitySection(GetGatewayData(context).Parameters.OwnerIdentity, GetGatewayData(context).IsMinimal)))
            .Add(new LambdaPromptSection(120, static context => buildTimeSection(GetGatewayData(context).Parameters.UserTimezone)))
            .Add(new LambdaPromptSection(130, static _ => ["## Workspace Files (injected)", "These user-editable files are loaded by BotNexus and included below in Project Context.", string.Empty]))
            .Add(new LambdaPromptSection(140, static context => buildReplyTagsSection(GetGatewayData(context).IsMinimal)))
            .Add(new LambdaPromptSection(150, static context => buildMessagingSection(GetGatewayData(context).IsMinimal, GetGatewayData(context).NormalizedTools, GetGatewayData(context).RuntimeChannel, GetGatewayData(context).InlineButtonsEnabled)))
            .Add(new LambdaPromptSection(160, static context => buildVoiceSection(GetGatewayData(context).IsMinimal, GetGatewayData(context).Parameters.TtsHint)))
            .Add(new LambdaPromptSection(170, BuildReasoningSection, static context => GetGatewayData(context).Parameters.ReasoningTagHint))
            .Add(new LambdaPromptSection(180, static context => buildProjectContextSection(GetGatewayData(context).StableContextFiles, "# Project Context", dynamic: false)))
            .Add(new LambdaPromptSection(190, BuildSilentRepliesSection, static context => !GetGatewayData(context).IsMinimal))
            .Add(new LambdaPromptSection(200, static _ => [SystemPromptCacheBoundary]))
            .Add(new LambdaPromptSection(210, static context => buildProjectContextSection(
                GetGatewayData(context).DynamicContextFiles,
                GetGatewayData(context).StableContextFiles.Count > 0 ? "# Dynamic Project Context" : "# Project Context",
                dynamic: true)))
            .Add(new LambdaPromptSection(220, BuildExtraSystemPromptSection))
            .Add(new LambdaPromptSection(230, BuildHeartbeatSection, static context => !GetGatewayData(context).IsMinimal))
            .Add(new LambdaPromptSection(240, BuildRuntimeSection));

        var lines = pipeline.BuildLines(promptContext);
        return string.Join("\n", lines.Where(static line => !string.IsNullOrEmpty(line)));
    }
    public static IReadOnlyList<ContextFile> sortContextFilesForPrompt(IReadOnlyList<ContextFile> contextFiles)
    {
        return ContextFileOrdering.SortForPrompt(contextFiles.Select(static file => new BotNexus.Prompts.ContextFile(file.Path, file.Content)).ToList())
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

    public static IReadOnlyList<string> buildSkillsSection(string? skillsPrompt, string readToolName)
    {
        var trimmed = NormalizeStructuredPromptSection(skillsPrompt);
        if (string.IsNullOrWhiteSpace(trimmed))
            return [];

        return
        [
            "## Skills (mandatory)",
            "Before replying: scan <available_skills> <description> entries.",
            $"- If exactly one skill clearly applies: read its SKILL.md at <location> with `{readToolName}`, then follow it.",
            "- If multiple could apply: choose the most specific one, then read/follow it.",
            "- If none clearly apply: do not read any SKILL.md.",
            "Constraints: never read more than one skill up front; only read after selecting.",
            "- When a skill drives external API writes, assume rate limits: prefer fewer larger writes, avoid tight one-item loops, serialize bursts when possible, and respect 429/Retry-After.",
            trimmed,
            ""
        ];
    }

    public static IReadOnlyList<string> buildMemorySection(bool isMinimal, IReadOnlySet<string> availableTools)
    {
        _ = availableTools;
        if (isMinimal)
            return [];

        // TODO: Memory plugin prompt injection not yet implemented.
        return [];
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
            "## Messaging",
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

    public static IReadOnlyList<string> buildExecutionBiasSection(bool isMinimal)
    {
        if (isMinimal)
            return [];

        return
        [
            "## Execution Bias",
            "If the user asks you to do the work, start doing it in the same turn.",
            "Use a real tool call or concrete action first when the task is actionable; do not stop at a plan or promise-to-act reply.",
            "Commentary-only turns are incomplete when tools are available and the next action is clear.",
            "If the work will take multiple steps or a while to finish, send one short progress update before or while acting.",
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
            Capabilities = runtime.Capabilities
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
            "## Tooling",
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
        return lines;
    }

    private static IReadOnlyList<string> BuildToolCallStyleSection(PromptContext context)
    {
        var data = GetGatewayData(context);
        return buildOverridablePromptSection(
            null,
            [
                "## Tool Call Style",
                "Default: do not narrate routine, low-risk tool calls (just call the tool).",
                "Narrate only when it helps: multi-step work, complex/challenging problems, sensitive actions (e.g., deletions), or when the user explicitly asks.",
                "Keep narration brief and value-dense; avoid repeating obvious steps.",
                "Use plain human language for narration unless in a technical context.",
                "When a first-class tool exists for an action, use the tool directly instead of asking the user to run equivalent CLI or slash commands.",
                buildExecApprovalPromptGuidance(data.RuntimeChannel, data.InlineButtonsEnabled),
                "Never execute /approve through exec or any other shell/tool path; /approve is a user-facing approval command, not a shell command.",
                "Treat allow-once as single-command only: if another elevated command needs approval, request a fresh /approve and do not claim prior approval covered it.",
                "When approvals are required, preserve and show the full command/script exactly as provided (including chained operators like &&, ||, |, ;, or multiline shells) so the user can approve what will actually run.",
                ""
            ]);
    }

    private static IReadOnlyList<string> BuildSafetyAndCliSection(PromptContext _)
    {
        return
        [
            "## Safety",
            "You have no independent goals: do not pursue self-preservation, replication, resource acquisition, or power-seeking; avoid long-term plans beyond the user's request.",
            "Prioritize safety and human oversight over completion; if instructions conflict, pause and ask; comply with stop/pause/audit requests and never bypass safeguards. (Inspired by Anthropic's constitution.)",
            "Do not manipulate or persuade anyone to expand access or disable safeguards. Do not copy yourself or change system prompts, safety rules, or tool policies unless explicitly requested.",
            "",
            "## BotNexus CLI Quick Reference",
            "BotNexus is controlled via subcommands. Do not invent commands.",
            "To manage the Gateway daemon service (start/stop/restart):",
            "- botnexus gateway status",
            "- botnexus gateway start",
            "- botnexus gateway stop",
            "- botnexus gateway restart",
            "If unsure, ask the user to run `botnexus help` (or `botnexus gateway --help`) and paste the output.",
            ""
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

        lines.Add("## Workspace");
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
            "## Silent Replies",
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
            "## Runtime",
            buildRuntimeLine(data.Parameters.Runtime),
            $"Reasoning: {(data.Parameters.ReasoningLevel ?? "off")} (hidden unless on/stream). Toggle /reasoning; /status shows Reasoning when enabled."
        ];
    }

    private sealed class LambdaPromptSection(
        int order,
        Func<PromptContext, IReadOnlyList<string>> build,
        Func<PromptContext, bool>? shouldInclude = null) : IPromptSection
    {
        public int Order => order;

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

    private static bool IsDynamicContextFile(string pathValue) =>
        ContextFileOrdering.IsDynamic(pathValue);

    private static string NormalizeContextFilePath(string pathValue) =>
        ContextFileOrdering.NormalizePath(pathValue);

    private static string GetContextFileBasename(string pathValue)
        => ContextFileOrdering.GetBasename(pathValue);
}

