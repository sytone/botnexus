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
    private static readonly Dictionary<string, int> ContextFileOrder = new(StringComparer.OrdinalIgnoreCase)
    {
        ["agents.md"] = 10,
        ["soul.md"] = 20,
        ["identity.md"] = 30,
        ["user.md"] = 40,
        ["tools.md"] = 50,
        ["bootstrap.md"] = 60,
        ["memory.md"] = 70
    };

    public static string Build(SystemPromptParams @params)
    {
        ArgumentNullException.ThrowIfNull(@params);

        if (@params.PromptMode == PromptMode.None)
            return "You are a personal assistant running inside BotNexus.";

        var rawToolNames = (@params.ToolNames ?? []).Select(static t => t?.Trim() ?? string.Empty).Where(static t => !string.IsNullOrWhiteSpace(t)).ToList();
        Dictionary<string, string> canonicalByNormalized = new(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in rawToolNames)
        {
            if (!canonicalByNormalized.ContainsKey(tool))
                canonicalByNormalized[tool.ToLowerInvariant()] = tool;
        }

        string ResolveToolName(string normalized) =>
            canonicalByNormalized.TryGetValue(normalized, out var value) ? value : normalized;

        var normalizedTools = rawToolNames.Select(static t => t.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        var isMinimal = @params.PromptMode is PromptMode.Minimal;
        var hasGateway = normalizedTools.Contains("gateway");
        var hasCronTool = normalizedTools.Contains("cron") || rawToolNames.Count == 0;
        var hasUpdatePlanTool = normalizedTools.Contains("update_plan");
        var readToolName = ResolveToolName("read");
        var bashToolName = ResolveToolName("bash");
        var runtimeChannel = @params.Runtime?.Channel?.Trim().ToLowerInvariant();
        var runtimeCapabilities = NormalizePromptCapabilityIds(@params.Runtime?.Capabilities ?? []);
        var inlineButtonsEnabled = runtimeCapabilities.Contains("inlinebuttons", StringComparer.Ordinal);

        var lines = new List<string>
        {
            "You are a personal assistant running inside BotNexus.",
            "",
            "## Tooling",
            "Structured tool definitions are the source of truth for tool names, descriptions, and parameters.",
            "Tool names are case-sensitive. Call tools exactly as listed in the structured tool definitions.",
            "If a tool is present in the structured tool definitions, it is available unless a later tool call reports a policy/runtime restriction.",
            "TOOLS.md does not control tool availability; it is user guidance for how to use external tools."
        };

        lines.AddRange(hasCronTool
            ? [
                $"For follow-up at a future time (for example \"check back in 10 minutes\", reminders, run-later work, or recurring tasks), use cron instead of {bashToolName} sleep or delay loops.",
                $"Use {bashToolName} only for commands that need to run now.",
                $"For long-running work that starts now, start it once and rely on automatic completion wake when it is enabled and the command emits output or fails; otherwise use {bashToolName} to check status.",
                "Do not emulate scheduling with sleep loops, timeout loops, or repeated polling."
            ]
            : [
                $"For long waits, avoid rapid poll loops: use {bashToolName} with appropriate timeouts.",
                $"For long-running work that starts now, start it once and rely on automatic completion wake when it is enabled and the command emits output or fails; otherwise use {bashToolName} to check status."
            ]);

        if (hasUpdatePlanTool)
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
        // TODO: ACP support not yet implemented.
        // For requests like "do this in codex/claude code/cursor/gemini" or similar ACP harnesses, treat it as ACP harness intent and call `sessions_spawn` with `runtime: "acp"`.
        // On Discord, default ACP harness requests to thread-bound persistent sessions (`thread: true`, `mode: "session"`) unless the user asks otherwise.
        // Set `agentId` explicitly unless `acp.defaultAgent` is configured, and do not route ACP harness requests through `subagents`/`agents_list` or local PTY exec flows.
        // For ACP harness thread spawns, do not call `message` with `action=thread-create`; use `sessions_spawn` (`runtime: "acp"`, `thread: true`) as the single thread creation path.
        lines.Add("Do not poll `subagents list` / `sessions_list` in a loop; only check status on-demand (for intervention, debugging, or when explicitly asked).");
        lines.Add(string.Empty);

        lines.AddRange(buildOverridablePromptSection(null, []));
        lines.AddRange(buildOverridablePromptSection(
            null,
            [
                "## Tool Call Style",
                "Default: do not narrate routine, low-risk tool calls (just call the tool).",
                "Narrate only when it helps: multi-step work, complex/challenging problems, sensitive actions (e.g., deletions), or when the user explicitly asks.",
                "Keep narration brief and value-dense; avoid repeating obvious steps.",
                "Use plain human language for narration unless in a technical context.",
                "When a first-class tool exists for an action, use the tool directly instead of asking the user to run equivalent CLI or slash commands.",
                buildBashApprovalPromptGuidance(runtimeChannel, inlineButtonsEnabled),
                "Never execute /approve through bash or any other shell/tool path; /approve is a user-facing approval command, not a shell command.",
                "Treat allow-once as single-command only: if another elevated command needs approval, request a fresh /approve and do not claim prior approval covered it.",
                "When approvals are required, preserve and show the full command/script exactly as provided (including chained operators like &&, ||, |, ;, or multiline shells) so the user can approve what will actually run.",
                ""
            ]));

        lines.AddRange(buildOverridablePromptSection(null, buildExecutionBiasSection(isMinimal)));
        lines.AddRange(
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
        ]);

        lines.AddRange(buildSkillsSection(@params.SkillsPrompt, readToolName));
        lines.AddRange(buildMemorySection(isMinimal, normalizedTools));

        if (hasGateway && !isMinimal)
        {
            lines.Add("## BotNexus Self-Update");
            lines.AddRange(
            [
                "Get Updates (self-update) is ONLY allowed when the user explicitly asks for it.",
                "Do not run config.apply or update.run unless the user explicitly requests an update or config change; if it's not explicit, ask first.",
                "Use config.schema.lookup with a specific dot path to inspect only the relevant config subtree before making config changes or answering config-field questions; avoid guessing field names/types.",
                "Actions: config.schema.lookup, config.get, config.apply (validate + write full config, then restart), config.patch (partial update, merges with existing), update.run (update deps or git, then restart).",
                "After restart, BotNexus pings the last active session automatically.",
                ""
            ]);
        }

        lines.Add(string.Empty);
        if (@params.ModelAliasLines is { Count: > 0 } && !isMinimal)
        {
            lines.Add("## Model Aliases");
            lines.Add("Prefer aliases when specifying model overrides; full provider/model is also accepted.");
            lines.AddRange(@params.ModelAliasLines.Select(NormalizeStructuredPromptSection).Where(static s => !string.IsNullOrWhiteSpace(s)));
            lines.Add(string.Empty);
        }

        if (!string.IsNullOrWhiteSpace(@params.UserTimezone))
            lines.Add("If you need the current date, time, or day of week, run session_status (📊 session_status).");

        lines.Add("## Workspace");
        lines.Add($"Your working directory is: {@params.WorkspaceDir}");
        lines.Add("Treat this directory as the single global workspace for file operations unless explicitly instructed otherwise.");
        lines.AddRange((@params.WorkspaceNotes ?? []).Select(NormalizeStructuredPromptSection).Where(static s => !string.IsNullOrWhiteSpace(s)));
        lines.Add(string.Empty);
        lines.AddRange(buildDocsSection(@params.DocsPath, isMinimal, readToolName));
        // TODO: Sandbox support not yet implemented.
        // ## Sandbox
        // You are running in a sandboxed runtime (tools execute in Docker).
        // Some tools may be unavailable due to sandbox policy.
        // Sub-agents stay sandboxed (no elevated/host access). Need outside-sandbox read/write? Don't spawn; ask first.
        lines.AddRange(buildUserIdentitySection(@params.OwnerIdentity, isMinimal));
        lines.AddRange(buildTimeSection(@params.UserTimezone));
        lines.Add("## Workspace Files (injected)");
        lines.Add("These user-editable files are loaded by BotNexus and included below in Project Context.");
        lines.Add(string.Empty);
        lines.AddRange(buildReplyTagsSection(isMinimal));
        lines.AddRange(buildMessagingSection(isMinimal, normalizedTools, runtimeChannel, inlineButtonsEnabled));
        lines.AddRange(buildVoiceSection(isMinimal, @params.TtsHint));
        // TODO: Reaction guidance injection not yet implemented.
        // ## Reactions
        // Reactions are enabled for {channel} in MINIMAL/EXTENSIVE mode...
        if (@params.ReasoningTagHint)
        {
            lines.Add("## Reasoning Format");
            lines.Add(BuildReasoningHint());
            lines.Add(string.Empty);
        }

        var contextFiles = (@params.ContextFiles ?? []).Where(static file => !string.IsNullOrWhiteSpace(file.Path)).ToList();
        var orderedContextFiles = sortContextFilesForPrompt(contextFiles);
        var stableContextFiles = orderedContextFiles.Where(static file => !IsDynamicContextFile(file.Path)).ToList();
        var dynamicContextFiles = orderedContextFiles.Where(static file => IsDynamicContextFile(file.Path)).ToList();
        lines.AddRange(buildProjectContextSection(stableContextFiles, "# Project Context", dynamic: false));

        if (!isMinimal)
        {
            lines.AddRange(
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
            ]);
        }

        lines.Add(SystemPromptCacheBoundary);
        lines.AddRange(
            buildProjectContextSection(
                dynamicContextFiles,
                stableContextFiles.Count > 0 ? "# Dynamic Project Context" : "# Project Context",
                dynamic: true));

        var extraSystemPrompt = NormalizeStructuredPromptSection(@params.ExtraSystemPrompt);
        if (!string.IsNullOrWhiteSpace(extraSystemPrompt))
        {
            lines.Add(@params.PromptMode == PromptMode.Minimal ? "## Subagent Context" : "## Group Chat Context");
            lines.Add(extraSystemPrompt);
            lines.Add(string.Empty);
        }

        var heartbeatPrompt = NormalizeStructuredPromptSection(@params.HeartbeatPrompt);
        if (!isMinimal && !string.IsNullOrWhiteSpace(heartbeatPrompt))
        {
            lines.AddRange(
            [
                "## Heartbeats",
                $"Heartbeat prompt: {heartbeatPrompt}",
                "If you receive a heartbeat poll (a user message matching the heartbeat prompt above), and there is nothing that needs attention, reply exactly:",
                "HEARTBEAT_OK",
                "BotNexus treats a leading/trailing \"HEARTBEAT_OK\" as a heartbeat ack (and may discard it).",
                "If something needs attention, do NOT include \"HEARTBEAT_OK\"; reply with the alert text instead.",
                ""
            ]);
        }

        lines.Add("## Runtime");
        lines.Add(buildRuntimeLine(@params.Runtime));
        lines.Add($"Reasoning: {(@params.ReasoningLevel ?? "off")} (hidden unless on/stream). Toggle /reasoning; /status shows Reasoning when enabled.");

        return string.Join("\n", lines.Where(static line => !string.IsNullOrEmpty(line)));
    }

    public static IReadOnlyList<ContextFile> sortContextFilesForPrompt(IReadOnlyList<ContextFile> contextFiles)
    {
        return contextFiles.OrderBy(file => ContextFileOrder.TryGetValue(GetContextFileBasename(file.Path), out var order) ? order : int.MaxValue)
            .ThenBy(file => GetContextFileBasename(file.Path), StringComparer.Ordinal)
            .ThenBy(file => NormalizeContextFilePath(file.Path), StringComparer.Ordinal)
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
            "- Never use bash/curl for provider messaging; BotNexus handles all routing internally."
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
        var normalizedRuntimeCapabilities = NormalizePromptCapabilityIds(runtime?.Capabilities ?? []);
        var parts = new List<string>
        {
            !string.IsNullOrWhiteSpace(runtime?.AgentId) ? $"agent={runtime!.AgentId}" : string.Empty,
            !string.IsNullOrWhiteSpace(runtime?.Host) ? $"host={runtime!.Host}" : string.Empty,
            !string.IsNullOrWhiteSpace(runtime?.Os)
                ? $"os={runtime!.Os}{(!string.IsNullOrWhiteSpace(runtime.Arch) ? $" ({runtime.Arch})" : string.Empty)}"
                : !string.IsNullOrWhiteSpace(runtime?.Arch)
                    ? $"arch={runtime!.Arch}"
                    : string.Empty,
            !string.IsNullOrWhiteSpace(runtime?.Provider) ? $"provider={runtime!.Provider}" : string.Empty,
            !string.IsNullOrWhiteSpace(runtime?.Model) ? $"model={runtime!.Model}" : string.Empty,
            !string.IsNullOrWhiteSpace(runtime?.DefaultModel) ? $"default_model={runtime!.DefaultModel}" : string.Empty,
            !string.IsNullOrWhiteSpace(runtime?.Shell) ? $"shell={runtime!.Shell}" : string.Empty,
            !string.IsNullOrWhiteSpace(runtime?.Channel) ? $"channel={runtime!.Channel.Trim().ToLowerInvariant()}" : string.Empty,
            !string.IsNullOrWhiteSpace(runtime?.Channel)
                ? $"capabilities={(normalizedRuntimeCapabilities.Count > 0 ? string.Join(",", normalizedRuntimeCapabilities) : "none")}"
                : string.Empty
        };

        return $"Runtime: {string.Join(" | ", parts.Where(static value => !string.IsNullOrWhiteSpace(value)))}";
    }

    public static IReadOnlyList<string> buildOverridablePromptSection(string? overrideValue, IReadOnlyList<string> fallback)
    {
        var overrideSection = NormalizeStructuredPromptSection(overrideValue);
        if (!string.IsNullOrWhiteSpace(overrideSection))
            return [overrideSection, ""];

        return fallback;
    }

    private static string buildBashApprovalPromptGuidance(string? runtimeChannel, bool inlineButtonsEnabled)
    {
        var usesNativeApprovalUi = string.Equals(runtimeChannel, "webchat", StringComparison.OrdinalIgnoreCase)
            || inlineButtonsEnabled;
        return usesNativeApprovalUi
            ? "When bash returns approval-pending on this channel, rely on native approval card/buttons when they appear and do not also send plain chat /approve instructions. Only include the concrete /approve command if the tool result says chat approvals are unavailable or only manual approval is possible."
            : "When bash returns approval-pending, include the concrete /approve command from tool output as plain chat text for the user, and do not ask for a different or rotated code.";
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
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = value.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n').Select(static line => line.TrimEnd());
        return string.Join("\n", lines).Trim();
    }

    private static IReadOnlyList<string> NormalizePromptCapabilityIds(IEnumerable<string> capabilities)
    {
        return capabilities.Select(capability => capability.Trim().ToLowerInvariant())
            .Where(static capability => capability.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static capability => capability, StringComparer.Ordinal)
            .ToList();
    }

    private static bool IsDynamicContextFile(string pathValue) =>
        string.Equals(GetContextFileBasename(pathValue), "heartbeat.md", StringComparison.Ordinal);

    private static string NormalizeContextFilePath(string pathValue) =>
        pathValue.Trim().Replace('\\', '/');

    private static string GetContextFileBasename(string pathValue)
    {
        var normalizedPath = NormalizeContextFilePath(pathValue);
        var segments = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return (segments.LastOrDefault() ?? normalizedPath).ToLowerInvariant();
    }
}
