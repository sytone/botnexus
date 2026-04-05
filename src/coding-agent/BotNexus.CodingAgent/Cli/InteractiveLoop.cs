using System.Text.Json;
using BotNexus.AgentCore;
using BotNexus.AgentCore.Types;
using BotNexus.CodingAgent.Auth;
using BotNexus.CodingAgent.Extensions;
using BotNexus.CodingAgent.Session;
using BotNexus.Providers.Core;
using BotNexus.Providers.Core.Registry;

namespace BotNexus.CodingAgent.Cli;

/// <summary>
/// Interactive prompt-response loop for terminal conversations.
/// </summary>
public sealed class InteractiveLoop
{
    public async Task RunAsync(
        Agent agent,
        CodingAgentConfig config,
        LlmClient llmClient,
        ModelRegistry modelRegistry,
        AuthManager authManager,
        ExtensionRunner extensionRunner,
        SessionManager sessionManager,
        SessionInfo session,
        OutputFormatter output,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(llmClient);
        ArgumentNullException.ThrowIfNull(extensionRunner);
        ArgumentNullException.ThrowIfNull(sessionManager);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(output);

        var currentSession = session;
        var sessionCompactor = new SessionCompactor();
        await extensionRunner
            .OnSessionStartAsync(new SessionLifecycleContext(currentSession, currentSession.WorkingDirectory, agent.State.Model.Id), ct)
            .ConfigureAwait(false);
        output.WriteWelcome(agent.State.Model.Id, currentSession);

        using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        CancellationTokenSource? promptCts = null;
        var writeLineBeforePrompt = true;

        ConsoleCancelEventHandler cancelHandler = (_, args) =>
        {
            args.Cancel = true;
            if (agent.Status == AgentStatus.Running)
            {
                promptCts?.Cancel();
                output.WriteError("Cancelled current operation.");
            }
        };

        Console.CancelKeyPress += cancelHandler;
        using var subscription = agent.Subscribe(async (@event, eventCt) =>
        {
            switch (@event)
            {
                case MessageUpdateEvent update when !string.IsNullOrEmpty(update.ContentDelta):
                    output.WriteAssistantText(update.ContentDelta!);
                    break;
                case ToolExecutionStartEvent toolStart:
                    output.WriteToolStart(toolStart.ToolName, JsonSerializer.Serialize(toolStart.Args));
                    break;
                case ToolExecutionEndEvent toolEnd:
                    output.WriteToolEnd(toolEnd.ToolName, !toolEnd.IsError);
                    break;
                case TurnEndEvent:
                    await CompactIfNeededAsync(agent, config, llmClient, authManager, extensionRunner, sessionCompactor, eventCt).ConfigureAwait(false);
                    output.WriteTurnSeparator();
                    currentSession = UpdateSessionSnapshot(currentSession, agent);
                    await sessionManager.SaveSessionAsync(currentSession, agent.State.Messages).ConfigureAwait(false);
                    writeLineBeforePrompt = false;
                    break;
            }

            _ = eventCt;
        });

        try
        {
            while (!loopCts.IsCancellationRequested)
            {
                if (writeLineBeforePrompt)
                {
                    Console.WriteLine();
                }

                writeLineBeforePrompt = true;
                Console.Write("> ");
                var input = Console.ReadLine();
                if (input is null)
                {
                    break;
                }

                var trimmed = input.Trim();
                if (string.IsNullOrWhiteSpace(trimmed))
                {
                    continue;
                }

                if (await HandleCommandAsync(trimmed, agent, config, modelRegistry, authManager, output, sessionManager, currentSession) is { } updatedSession)
                {
                    currentSession = updatedSession;
                    if (trimmed.Equals("/quit", StringComparison.OrdinalIgnoreCase) ||
                        trimmed.Equals("/exit", StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }

                    continue;
                }

                promptCts = CancellationTokenSource.CreateLinkedTokenSource(loopCts.Token);
                try
                {
                    await agent.PromptAsync(new UserMessage(input), promptCts.Token).ConfigureAwait(false);
                    currentSession = UpdateSessionSnapshot(currentSession, agent);
                    await sessionManager.SaveSessionAsync(currentSession, agent.State.Messages).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (promptCts.IsCancellationRequested)
                {
                    await agent.AbortAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    output.WriteError($"Error: {ex.Message}");
                }
                finally
                {
                    promptCts.Dispose();
                    promptCts = null;
                }
            }
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            currentSession = UpdateSessionSnapshot(currentSession, agent);
            await sessionManager.SaveSessionAsync(currentSession, agent.State.Messages).ConfigureAwait(false);
            await extensionRunner
                .OnSessionEndAsync(new SessionLifecycleContext(currentSession, currentSession.WorkingDirectory, agent.State.Model.Id), ct)
                .ConfigureAwait(false);
        }
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

    /// <summary>
    /// Handle slash commands. Returns the updated session if a command was handled, null otherwise.
    /// </summary>
    private static async Task<SessionInfo?> HandleCommandAsync(
        string input,
        Agent agent,
        CodingAgentConfig config,
        ModelRegistry modelRegistry,
        AuthManager authManager,
        OutputFormatter output,
        SessionManager sessionManager,
        SessionInfo session)
    {
        if (input.Equals("/quit", StringComparison.OrdinalIgnoreCase) ||
            input.Equals("/exit", StringComparison.OrdinalIgnoreCase))
        {
            return session;
        }

        if (input.Equals("/login", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                await authManager.LoginAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                output.WriteError($"Login failed: {ex.Message}");
            }

            return session;
        }

        if (input.Equals("/logout", StringComparison.OrdinalIgnoreCase))
        {
            authManager.Logout();
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("Logged out. Credentials removed from auth.json.");
            Console.ResetColor();
            return session;
        }

        if (input.Equals("/clear", StringComparison.OrdinalIgnoreCase))
        {
            agent.Reset();
            var updated = UpdateSessionSnapshot(session, agent);
            output.WriteSessionInfo(updated);
            return updated;
        }

        if (input.Equals("/session", StringComparison.OrdinalIgnoreCase))
        {
            var updated = UpdateSessionSnapshot(session, agent);
            output.WriteSessionInfo(updated);
            return updated;
        }

        if (input.Equals("/help", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("Commands:");
            Console.WriteLine("  /login        Authenticate with GitHub Copilot (OAuth device flow)");
            Console.WriteLine("  /logout       Remove saved credentials");
            Console.WriteLine("  /model <name> Switch model");
            Console.WriteLine("  /session      Show session info");
            Console.WriteLine("  /clear        Reset conversation");
            Console.WriteLine("  /quit         Exit");
            return session;
        }

        if (input.StartsWith("/model ", StringComparison.OrdinalIgnoreCase))
        {
            var modelId = input[7..].Trim();
            if (string.IsNullOrWhiteSpace(modelId))
            {
                output.WriteError("Usage: /model <name>");
                return session;
            }

            var provider = config.Provider ?? agent.State.Model.Provider;
            var previousModel = agent.State.Model.Id;
            agent.State.Model = ResolveModel(provider, modelId, config.MaxContextTokens, modelRegistry);
            var updated = UpdateSessionSnapshot(session, agent);
            if (!string.Equals(previousModel, agent.State.Model.Id, StringComparison.Ordinal))
            {
                updated = await sessionManager.WriteMetadataAsync(
                        updated,
                        "model_change",
                        $"{previousModel} → {agent.State.Model.Id}")
                    .ConfigureAwait(false);
            }

            output.WriteSessionInfo(updated);
            return updated;
        }

        return null;
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

    private static BotNexus.Providers.Core.Models.LlmModel ResolveModel(
        string provider,
        string modelId,
        int maxContextTokens,
        ModelRegistry modelRegistry)
    {
        if (provider.Equals("copilot", StringComparison.OrdinalIgnoreCase))
        {
            provider = "github-copilot";
        }

        var existing = modelRegistry.GetModel(provider, modelId);
        if (existing is not null)
        {
            return existing;
        }

        return new BotNexus.Providers.Core.Models.LlmModel(
            Id: modelId,
            Name: modelId,
            Api: "openai-completions",
            Provider: provider,
            BaseUrl: "https://api.individual.githubcopilot.com",
            Reasoning: false,
            Input: ["text"],
            Cost: new BotNexus.Providers.Core.Models.ModelCost(0, 0, 0, 0),
            ContextWindow: maxContextTokens,
            MaxTokens: Math.Min(8192, maxContextTokens),
            Headers: new Dictionary<string, string>
            {
                ["User-Agent"] = "GitHubCopilotChat/0.35.0",
                ["Editor-Version"] = "vscode/1.107.0",
                ["Editor-Plugin-Version"] = "copilot-chat/0.35.0",
                ["Copilot-Integration-Id"] = "vscode-chat"
            });
    }

}
