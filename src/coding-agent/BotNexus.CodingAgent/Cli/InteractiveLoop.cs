using System.Text.Json;
using BotNexus.AgentCore;
using BotNexus.AgentCore.Types;
using BotNexus.CodingAgent.Session;
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
        SessionManager sessionManager,
        SessionInfo session,
        OutputFormatter output,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(sessionManager);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(output);

        var currentSession = session;
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

                if (HandleCommand(trimmed, agent, config, output, ref currentSession))
                {
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
        }
    }

    private static bool HandleCommand(
        string input,
        Agent agent,
        CodingAgentConfig config,
        OutputFormatter output,
        ref SessionInfo session)
    {
        if (input.Equals("/quit", StringComparison.OrdinalIgnoreCase) ||
            input.Equals("/exit", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (input.Equals("/clear", StringComparison.OrdinalIgnoreCase))
        {
            agent.Reset();
            session = UpdateSessionSnapshot(session, agent);
            output.WriteSessionInfo(session);
            return true;
        }

        if (input.Equals("/session", StringComparison.OrdinalIgnoreCase))
        {
            session = UpdateSessionSnapshot(session, agent);
            output.WriteSessionInfo(session);
            return true;
        }

        if (input.Equals("/help", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("Commands: /help, /session, /clear, /model <name>, /quit");
            return true;
        }

        if (input.StartsWith("/model ", StringComparison.OrdinalIgnoreCase))
        {
            var modelId = input[7..].Trim();
            if (string.IsNullOrWhiteSpace(modelId))
            {
                output.WriteError("Usage: /model <name>");
                return true;
            }

            var provider = config.Provider ?? agent.State.Model.Provider;
            agent.State.Model = ResolveModel(provider, modelId, config.MaxContextTokens);
            output.WriteSessionInfo(UpdateSessionSnapshot(session, agent));
            return true;
        }

        return false;
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

    private static BotNexus.Providers.Core.Models.LlmModel ResolveModel(string provider, string modelId, int maxContextTokens)
    {
        var existing = ModelRegistry.GetModel(provider, modelId);
        if (existing is not null)
        {
            return existing;
        }

        return new BotNexus.Providers.Core.Models.LlmModel(
            Id: modelId,
            Name: modelId,
            Api: provider,
            Provider: provider,
            BaseUrl: string.Empty,
            Reasoning: false,
            Input: ["text"],
            Cost: new BotNexus.Providers.Core.Models.ModelCost(0, 0, 0, 0),
            ContextWindow: maxContextTokens,
            MaxTokens: Math.Min(8192, maxContextTokens));
    }
}
