using BotNexus.AgentCore;
using BotNexus.AgentCore.Types;
using BotNexus.CodingAgent.Auth;
using BotNexus.CodingAgent.Cli;
using BotNexus.CodingAgent.Extensions;
using BotNexus.CodingAgent.Session;

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

        var workingDirectory = Directory.GetCurrentDirectory();
        var config = CodingAgentConfig.Load(workingDirectory);
        CodingAgentConfig.EnsureDirectories(workingDirectory);
        ApplyOverrides(config, command);

        var authManager = new AuthManager(config.ConfigDirectory);
        var extensionTools = new ExtensionLoader().LoadExtensions(config.ExtensionsDirectory);
        var skills = new SkillsLoader().LoadSkills(workingDirectory, config);
        var sessionManager = new SessionManager();
        var output = new OutputFormatter();

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

        var agent = await CodingAgent.CreateAsync(config, workingDirectory, authManager, extensionTools, skills).ConfigureAwait(false);
        if (resumedMessages.Count > 0)
        {
            agent.State.Messages = resumedMessages;
        }

        session = session with
        {
            WorkingDirectory = Path.GetFullPath(workingDirectory),
            Model = agent.State.Model.Id
        };

        var runSinglePrompt = command.NonInteractive || !string.IsNullOrWhiteSpace(command.InitialPrompt);
        if (runSinglePrompt)
        {
            if (string.IsNullOrWhiteSpace(command.InitialPrompt))
            {
                Console.Error.WriteLine("A prompt is required in non-interactive mode.");
                return 1;
            }

            output.WriteWelcome(agent.State.Model.Id, session);
            await RunSinglePromptAsync(agent, command.InitialPrompt, output).ConfigureAwait(false);
            session = UpdateSessionSnapshot(session, agent);
            await sessionManager.SaveSessionAsync(session, agent.State.Messages).ConfigureAwait(false);
            return 0;
        }

        if (!authManager.HasCredentials() && string.IsNullOrWhiteSpace(config.ApiKey))
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("⚠ No credentials found. Use /login to authenticate with GitHub Copilot.");
            Console.ResetColor();
            Console.WriteLine();
        }

        var loop = new InteractiveLoop();
        await loop.RunAsync(agent, config, authManager, sessionManager, session, output, CancellationToken.None).ConfigureAwait(false);
        return 0;
    }

    private static async Task RunSinglePromptAsync(Agent agent, string prompt, OutputFormatter output)
    {
        using var subscription = agent.Subscribe((@event, ct) =>
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
                    output.WriteTurnSeparator();
                    break;
            }

            _ = ct;
            return Task.CompletedTask;
        });

        await agent.PromptAsync(new UserMessage(prompt)).ConfigureAwait(false);
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
}
