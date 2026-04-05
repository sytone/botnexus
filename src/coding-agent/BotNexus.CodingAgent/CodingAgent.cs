using System.Reflection;
using System.Text.Json;
using BotNexus.AgentCore;
using BotNexus.AgentCore.Configuration;
using BotNexus.AgentCore.Hooks;
using BotNexus.AgentCore.Tools;
using BotNexus.AgentCore.Types;
using BotNexus.CodingAgent.Auth;
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

        var promptBuilder = new SystemPromptBuilder();
        var systemPrompt = promptBuilder.Build(new SystemPromptContext(
            WorkingDirectory: root,
            GitBranch: gitBranch,
            GitStatus: gitStatus,
            PackageManager: packageManager,
            ToolNames: tools.Select(tool => tool.Name).ToList(),
            Skills: skills ?? [],
            CustomInstructions: null));

        // Resolve initial API key via AuthManager (auto-refreshes saved creds)
        var model = ResolveModel(config);
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
            ConvertToLlm: BuildConvertToLlmDelegate(),
            TransformContext: (messages, _) => Task.FromResult(messages),
            GetApiKey: async (provider, ct) =>
                await capturedAuthManager.GetApiKeyAsync(capturedConfig, provider, ct),
            GetSteeringMessages: null,
            GetFollowUpMessages: null,
            ToolExecutionMode: ToolExecutionMode.Sequential,
            BeforeToolCall: (context, _) => ExecuteBeforeHookAsync(context, safetyHooks, auditHooks, config),
            AfterToolCall: (context, _) => auditHooks.AuditAsync(context),
            GenerationSettings: new SimpleStreamOptions
            {
                MaxTokens = model.MaxTokens
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
        CodingAgentConfig config)
    {
        auditHooks.RegisterToolCallStart(context.ToolCallRequest.Id);
        return safetyHooks.ValidateAsync(context, config);
    }

    private static IReadOnlyList<IAgentTool> CreateTools(string workingDirectory, IReadOnlyList<IAgentTool>? extensionTools)
    {
        var tools = new List<IAgentTool>
        {
            new ReadTool(workingDirectory),
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

    private static ConvertToLlmDelegate BuildConvertToLlmDelegate()
    {
        var method = Type.GetType("BotNexus.AgentCore.Loop.MessageConverter, BotNexus.AgentCore")
            ?.GetMethod("ToProviderMessages", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        if (method is null)
        {
            throw new InvalidOperationException("MessageConverter.ToProviderMessages was not found.");
        }

        return (messages, _) =>
        {
            var converted = method.Invoke(null, [messages]) as IReadOnlyList<Message>;
            if (converted is null)
            {
                throw new InvalidOperationException("Message conversion returned null.");
            }

            return Task.FromResult(converted);
        };
    }

    private static LlmModel ResolveModel(CodingAgentConfig config)
    {
        var provider = config.Provider ?? "copilot";
        var modelId = config.Model ?? "gpt-4.1";

        var existing = ModelRegistry.GetModel(provider, modelId);
        if (existing is not null)
        {
            return existing;
        }

        return new LlmModel(
            Id: modelId,
            Name: modelId,
            Api: provider,
            Provider: provider,
            BaseUrl: string.Empty,
            Reasoning: false,
            Input: ["text"],
            Cost: new ModelCost(0, 0, 0, 0),
            ContextWindow: config.MaxContextTokens,
            MaxTokens: Math.Min(8192, config.MaxContextTokens));
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
