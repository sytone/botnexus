using System.Text.Json;
using BotNexus.AgentCore;
using BotNexus.AgentCore.Configuration;
using BotNexus.AgentCore.Hooks;
using BotNexus.AgentCore.Tools;
using BotNexus.AgentCore.Types;
using BotNexus.CodingAgent.Auth;
using BotNexus.CodingAgent.Extensions;
using BotNexus.CodingAgent.Hooks;
using BotNexus.CodingAgent.Tools;
using BotNexus.CodingAgent.Utils;
using BotNexus.Providers.Core;
using BotNexus.Providers.Core.Models;
using BotNexus.Providers.Core.Registry;

namespace BotNexus.CodingAgent;

public static class CodingAgent
{
    public static async Task<Agent> CreateAsync(
        CodingAgentConfig config,
        string workingDirectory,
        AuthManager authManager,
        LlmClient llmClient,
        ModelRegistry modelRegistry,
        ExtensionRunner? extensionRunner = null,
        IReadOnlyList<IAgentTool>? extensionTools = null,
        IReadOnlyList<string>? skills = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            throw new ArgumentException("Working directory cannot be empty.", nameof(workingDirectory));
        }

        var root = Path.GetFullPath(workingDirectory);
        CodingAgentConfig.EnsureDirectories(root);

        var tools = CreateTools(root, extensionTools);
        var gitBranch = await GitUtils.GetBranchAsync(root).ConfigureAwait(false);
        var gitStatus = await GitUtils.GetStatusAsync(root).ConfigureAwait(false);
        var packageManager = PackageManagerDetector.Detect(root);
        var contextFiles = await ContextFileDiscovery.DiscoverAsync(root, CancellationToken.None).ConfigureAwait(false);

        var promptBuilder = new SystemPromptBuilder();
        var systemPrompt = promptBuilder.Build(new SystemPromptContext(
            WorkingDirectory: root,
            GitBranch: gitBranch,
            GitStatus: gitStatus,
            PackageManager: packageManager,
            ToolNames: tools.Select(tool => tool.Name).ToList(),
            Skills: skills ?? [],
            CustomInstructions: null,
            ContextFiles: contextFiles));

        // Resolve initial API key via AuthManager (auto-refreshes saved creds)
        var model = ResolveModel(config, modelRegistry);
        var capturedAuthManager = authManager;
        var capturedConfig = config;

        var auditHooks = new AuditHooks(verbose: ResolveVerbose(config));
        var safetyHooks = new SafetyHooks();

        var options = new AgentOptions(
            InitialState: new AgentInitialState(
                SystemPrompt: systemPrompt,
                Model: model,
                Tools: tools),
            Model: model,
            LlmClient: llmClient,
            ConvertToLlm: DefaultMessageConverter.Create(),
            TransformContext: (messages, _) => Task.FromResult(messages),
            GetApiKey: async (provider, ct) =>
                await capturedAuthManager.GetApiKeyAsync(capturedConfig, provider, ct),
            GetSteeringMessages: null,
            GetFollowUpMessages: null,
            ToolExecutionMode: ToolExecutionMode.Sequential,
            BeforeToolCall: (context, ct) => ExecuteBeforeHookAsync(context, safetyHooks, auditHooks, extensionRunner, config, ct),
            AfterToolCall: (context, ct) => ExecuteAfterHookAsync(context, auditHooks, extensionRunner, ct),
            GenerationSettings: new SimpleStreamOptions
            {
                MaxTokens = model.MaxTokens,
                OnPayload = async (payload, payloadModel) =>
                    extensionRunner is null
                        ? payload
                        : await extensionRunner.OnModelRequestAsync(payload, payloadModel).ConfigureAwait(false)
            },
            SteeringMode: QueueMode.OneAtATime,
            FollowUpMode: QueueMode.OneAtATime,
            SessionId: null);

        return new Agent(options);
    }

    private static Task<BeforeToolCallResult?> ExecuteBeforeHookAsync(
        BeforeToolCallContext context,
        SafetyHooks safetyHooks,
        AuditHooks auditHooks,
        ExtensionRunner? extensionRunner,
        CodingAgentConfig config,
        CancellationToken cancellationToken)
    {
        auditHooks.RegisterToolCallStart(context.ToolCallRequest.Id);
        return ExecuteBeforeHookCoreAsync(context, safetyHooks, extensionRunner, config, cancellationToken);
    }

    private static async Task<BeforeToolCallResult?> ExecuteBeforeHookCoreAsync(
        BeforeToolCallContext context,
        SafetyHooks safetyHooks,
        ExtensionRunner? extensionRunner,
        CodingAgentConfig config,
        CancellationToken cancellationToken)
    {
        var safetyResult = await safetyHooks.ValidateAsync(context, config).ConfigureAwait(false);
        if (safetyResult?.Block == true)
        {
            return safetyResult;
        }

        if (extensionRunner is null)
        {
            return null;
        }

        return await extensionRunner.OnToolCallAsync(
                new ToolCallLifecycleContext(
                    ToolCallLifecycleStage.BeforeExecution,
                    context.ToolCallRequest.Id,
                    context.ToolCallRequest.Name,
                    context.ValidatedArgs),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<AfterToolCallResult?> ExecuteAfterHookAsync(
        AfterToolCallContext context,
        AuditHooks auditHooks,
        ExtensionRunner? extensionRunner,
        CancellationToken cancellationToken)
    {
        var auditResult = await auditHooks.AuditAsync(context).ConfigureAwait(false);
        if (extensionRunner is null)
        {
            return auditResult;
        }

        await extensionRunner.OnToolCallAsync(
                new ToolCallLifecycleContext(
                    ToolCallLifecycleStage.AfterExecution,
                    context.ToolCallRequest.Id,
                    context.ToolCallRequest.Name,
                    context.ValidatedArgs,
                    context.IsError),
                cancellationToken)
            .ConfigureAwait(false);

        var extensionResult = await extensionRunner.OnToolResultAsync(
                new ToolResultLifecycleContext(
                    context.ToolCallRequest.Id,
                    context.ToolCallRequest.Name,
                    context.ValidatedArgs,
                    context.Result,
                    context.IsError),
                cancellationToken)
            .ConfigureAwait(false);

        return MergeAfterResults(auditResult, extensionResult);
    }

    private static AfterToolCallResult? MergeAfterResults(AfterToolCallResult? first, AfterToolCallResult? second)
    {
        if (first is null)
        {
            return second;
        }

        if (second is null)
        {
            return first;
        }

        return new AfterToolCallResult(
            Content: second.Content ?? first.Content,
            Details: second.Details ?? first.Details,
            IsError: second.IsError ?? first.IsError);
    }

    private static IReadOnlyList<IAgentTool> CreateTools(string workingDirectory, IReadOnlyList<IAgentTool>? extensionTools)
    {
        var tools = new List<IAgentTool>
        {
            new ReadTool(workingDirectory),
            new ListDirectoryTool(workingDirectory),
            new WriteTool(workingDirectory),
            new EditTool(workingDirectory),
            new ShellTool(),
            new GlobTool(workingDirectory),
            new GrepTool(workingDirectory)
        };

        if (extensionTools is { Count: > 0 })
        {
            tools.AddRange(extensionTools);
        }

        return tools;
    }

    private static LlmModel ResolveModel(CodingAgentConfig config, ModelRegistry modelRegistry)
    {
        var provider = config.Provider ?? "github-copilot";
        var modelId = config.Model ?? "gpt-4.1";

        if (provider.Equals("copilot", StringComparison.OrdinalIgnoreCase))
        {
            provider = "github-copilot";
        }

        var existing = modelRegistry.GetModel(provider, modelId);
        if (existing is not null)
        {
            return existing;
        }

        return new LlmModel(
            Id: modelId,
            Name: modelId,
            Api: "openai-completions",
            Provider: provider,
            BaseUrl: "https://api.individual.githubcopilot.com",
            Reasoning: false,
            Input: ["text"],
            Cost: new ModelCost(0, 0, 0, 0),
            ContextWindow: config.MaxContextTokens,
            MaxTokens: Math.Min(8192, config.MaxContextTokens),
            Headers: new Dictionary<string, string>
            {
                ["User-Agent"] = "GitHubCopilotChat/0.35.0",
                ["Editor-Version"] = "vscode/1.107.0",
                ["Editor-Plugin-Version"] = "copilot-chat/0.35.0",
                ["Copilot-Integration-Id"] = "vscode-chat"
            });
    }

    private static bool ResolveVerbose(CodingAgentConfig config)
    {
        if (!config.Custom.TryGetValue("verbose", out var value) || value is null)
        {
            return false;
        }

        return value switch
        {
            bool flag => flag,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            string text when bool.TryParse(text, out var parsed) => parsed,
            _ => false
        };
    }
}


