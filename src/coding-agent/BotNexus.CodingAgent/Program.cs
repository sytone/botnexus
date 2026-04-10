using BotNexus.AgentCore;
using BotNexus.AgentCore.Types;
using BotNexus.CodingAgent.Auth;
using BotNexus.CodingAgent.Cli;
using BotNexus.CodingAgent.Extensions;
using BotNexus.CodingAgent.Session;
using BotNexus.Providers.Anthropic;
using BotNexus.Providers.Core;
using BotNexus.Providers.Core.Registry;
using BotNexus.Providers.OpenAI;
using BotNexus.Providers.OpenAICompat;
using System.IO.Abstractions;

namespace BotNexus.CodingAgent;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var parser = new CommandParser();
        CommandOptions command;

        try
        {
            command = parser.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.WriteLine(CommandParser.GetUsage());
            return 1;
        }

        if (command.ShowHelp)
        {
            Console.WriteLine(CommandParser.GetUsage());
            return 0;
        }

        var stdinPrompt = await ReadPipedStdinAsync().ConfigureAwait(false);
        var initialPrompt = CombinePrompt(stdinPrompt, command.InitialPrompt);
        var fileSystem = new FileSystem();
        var workingDirectory = Directory.GetCurrentDirectory();
        var config = CodingAgentConfig.Load(fileSystem, workingDirectory);
        CodingAgentConfig.EnsureDirectories(fileSystem, workingDirectory);
        ApplyOverrides(config, command);

        // Register all built-in API providers (matching pi-mono's registerBuiltInApiProviders)
        var (apiProviderRegistry, modelRegistry) = RegisterBuiltInProviders();
        var llmClient = new LlmClient(apiProviderRegistry, modelRegistry);

        var authManager = new AuthManager(config.ConfigDirectory, fileSystem);
        var extensionLoadResult = new ExtensionLoader(fileSystem).LoadExtensions(config.ExtensionsDirectory);
        var extensionRunner = new ExtensionRunner(extensionLoadResult.Extensions);
        var skills = new SkillsLoader(fileSystem).LoadSkills(workingDirectory, config);
        var sessionManager = new SessionManager(fileSystem);
        var nonInteractive = command.NonInteractive || !string.IsNullOrWhiteSpace(initialPrompt);
        var output = new OutputFormatter(nonInteractive);

        if (!string.IsNullOrWhiteSpace(command.LogPath))
        {
            var logDir = Path.GetDirectoryName(Path.GetFullPath(command.LogPath));
            if (!string.IsNullOrWhiteSpace(logDir))
            {
                fileSystem.Directory.CreateDirectory(logDir);
            }
            var logWriter = new StreamWriter(command.LogPath, append: false, new System.Text.UTF8Encoding(false))
            {
                AutoFlush = false
            };
            output.SetLogWriter(logWriter);
        }

        SessionInfo session;
        IReadOnlyList<AgentMessage> resumedMessages = [];
        if (!string.IsNullOrWhiteSpace(command.ResumeSessionId))
        {
            var resumed = await sessionManager.ResumeSessionAsync(command.ResumeSessionId, workingDirectory).ConfigureAwait(false);
            session = resumed.Session;
            resumedMessages = resumed.Messages;
        }
        else
        {
            session = await sessionManager.CreateSessionAsync(workingDirectory, "cli-session").ConfigureAwait(false);
        }

        var agent = await CodingAgent.CreateAsync(
            config,
            workingDirectory,
            authManager,
            llmClient,
            modelRegistry,
            fileSystem,
            extensionRunner,
            extensionLoadResult.Tools,
            skills,
            sessionManager,
            session).ConfigureAwait(false);
        if (resumedMessages.Count > 0)
        {
            agent.State.Messages = resumedMessages;
        }

        session = session with
        {
            WorkingDirectory = Path.GetFullPath(workingDirectory),
            Model = agent.State.Model.Id,
            Provider = agent.State.Model.Provider
        };

        var runSinglePrompt = command.NonInteractive || !string.IsNullOrWhiteSpace(initialPrompt);
        if (runSinglePrompt)
        {
            if (string.IsNullOrWhiteSpace(initialPrompt))
            {
                Console.Error.WriteLine("A prompt is required in non-interactive mode.");
                return 1;
            }

            await extensionRunner
                .OnSessionStartAsync(new SessionLifecycleContext(session, workingDirectory, agent.State.Model.Id))
                .ConfigureAwait(false);
            try
            {
                output.WriteWelcome(agent.State.Model.Id, session);
                await RunSinglePromptAsync(agent, initialPrompt, output, config, llmClient, authManager, extensionRunner).ConfigureAwait(false);
                session = UpdateSessionSnapshot(session, agent);
                await sessionManager.SaveSessionAsync(session, agent.State.Messages).ConfigureAwait(false);
                return 0;
            }
            finally
            {
                await extensionRunner
                    .OnSessionEndAsync(new SessionLifecycleContext(session, workingDirectory, agent.State.Model.Id))
                    .ConfigureAwait(false);
                output.Dispose();
            }
        }

        if (!authManager.HasCredentials() && string.IsNullOrWhiteSpace(config.ApiKey))
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("⚠ No credentials found. Use /login to authenticate with GitHub Copilot.");
            Console.ResetColor();
            Console.WriteLine();
        }

        try
        {
            var loop = new InteractiveLoop();
            await loop.RunAsync(
                agent,
                config,
                llmClient,
                modelRegistry,
                authManager,
                extensionRunner,
                sessionManager,
                session,
                output,
                CancellationToken.None).ConfigureAwait(false);
            return 0;
        }
        finally
        {
            output.Dispose();
        }
    }

    private static async Task RunSinglePromptAsync(
        Agent agent,
        string prompt,
        OutputFormatter output,
        CodingAgentConfig config,
        LlmClient llmClient,
        AuthManager authManager,
        ExtensionRunner extensionRunner)
    {
        var sessionCompactor = new SessionCompactor();
        using var subscription = agent.Subscribe(async (@event, ct) =>
        {
            switch (@event)
            {
                case MessageUpdateEvent update when !string.IsNullOrEmpty(update.ContentDelta):
                    output.WriteAssistantText(update.ContentDelta!);
                    break;
                case ToolExecutionStartEvent toolStart:
                    output.WriteToolStart(toolStart.ToolName, "{}");
                    break;
                case ToolExecutionEndEvent toolEnd:
                    output.WriteToolEnd(toolEnd.ToolName, !toolEnd.IsError);
                    break;
                case TurnEndEvent:
                    await CompactIfNeededAsync(agent, config, llmClient, authManager, extensionRunner, sessionCompactor, ct).ConfigureAwait(false);
                    output.WriteTurnSeparator();
                    break;
            }

            _ = ct;
        });

        await agent.PromptAsync(new UserMessage(prompt)).ConfigureAwait(false);
    }

    private static async Task CompactIfNeededAsync(
        Agent agent,
        CodingAgentConfig config,
        LlmClient llmClient,
        AuthManager authManager,
        ExtensionRunner extensionRunner,
        SessionCompactor compactor,
        CancellationToken cancellationToken)
    {
        var apiKey = await authManager.GetApiKeyAsync(config, agent.State.Model.Provider, cancellationToken).ConfigureAwait(false);
        var compacted = await compactor.CompactAsync(
                agent.State.Messages,
                new SessionCompactor.SessionCompactionOptions(
                    MaxContextTokens: config.MaxContextTokens,
                    ReserveTokens: Math.Max(2048, Math.Min(16384, config.MaxContextTokens / 5)),
                    KeepRecentTokens: Math.Max(4096, Math.Min(30000, config.MaxContextTokens / 4)),
                    KeepRecentCount: 10,
                    LlmClient: llmClient,
                    Model: agent.State.Model,
                    ApiKey: apiKey,
                    Headers: agent.State.Model.Headers,
                    OnCompactionAsync: async (context, hookCt) =>
                        await extensionRunner.OnCompactionAsync(
                                new CompactionLifecycleContext(
                                    context.MessagesToSummarize,
                                    context.RecentMessages,
                                    context.ReadFiles,
                                    context.ModifiedFiles,
                                    context.Summary),
                                hookCt)
                            .ConfigureAwait(false)),
                cancellationToken)
            .ConfigureAwait(false);

        if (ReferenceEquals(compacted, agent.State.Messages))
        {
            return;
        }

        agent.State.Messages = compacted;
    }

    private static void ApplyOverrides(CodingAgentConfig config, CommandOptions command)
    {
        if (!string.IsNullOrWhiteSpace(command.Model))
        {
            config.Model = command.Model;
        }

        if (!string.IsNullOrWhiteSpace(command.Provider))
        {
            config.Provider = command.Provider;
        }

        if (command.Verbose)
        {
            config.Custom["verbose"] = true;
        }

        if (command.ThinkingSpecified)
        {
            config.Custom["thinking"] = command.ThinkingLevel;
        }
    }

    private static SessionInfo UpdateSessionSnapshot(SessionInfo session, Agent agent)
    {
        return session with
        {
            UpdatedAt = DateTimeOffset.UtcNow,
            MessageCount = agent.State.Messages.Count,
            Model = agent.State.Model.Id
        };
    }

    private static async Task<string?> ReadPipedStdinAsync()
    {
        if (!Console.IsInputRedirected)
        {
            return null;
        }

        var input = await Console.In.ReadToEndAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        return input.Trim();
    }

    private static string? CombinePrompt(string? stdinPrompt, string? cliPrompt)
    {
        if (string.IsNullOrWhiteSpace(stdinPrompt))
        {
            return cliPrompt;
        }

        if (string.IsNullOrWhiteSpace(cliPrompt))
        {
            return stdinPrompt;
        }

        return $"{stdinPrompt}{cliPrompt}";
    }

    /// <summary>
    /// Register all built-in API providers with the global registry.
    /// Equivalent to pi-mono's registerBuiltInApiProviders() in register-builtins.ts.
    /// Must be called before any LlmClient usage.
    /// </summary>
    private static (ApiProviderRegistry ApiProviderRegistry, ModelRegistry ModelRegistry) RegisterBuiltInProviders()
    {
        var apiProviderRegistry = new ApiProviderRegistry();
        var modelRegistry = new ModelRegistry();
        new BuiltInModels().RegisterAll(modelRegistry);

        var httpClient = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        apiProviderRegistry.Register(new AnthropicProvider(httpClient));
        apiProviderRegistry.Register(new OpenAICompletionsProvider(httpClient, Microsoft.Extensions.Logging.Abstractions.NullLogger<OpenAICompletionsProvider>.Instance));
        apiProviderRegistry.Register(new OpenAIResponsesProvider(httpClient, Microsoft.Extensions.Logging.Abstractions.NullLogger<OpenAIResponsesProvider>.Instance));
        apiProviderRegistry.Register(new OpenAICompatProvider(httpClient));
        return (apiProviderRegistry, modelRegistry);
    }
}
