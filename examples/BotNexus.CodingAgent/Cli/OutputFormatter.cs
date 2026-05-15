using BotNexus.CodingAgent.Session;

namespace BotNexus.CodingAgent.Cli;

/// <summary>
/// Writes formatted terminal output for coding-agent interactions.
/// </summary>
public sealed class OutputFormatter : IDisposable
{
    private readonly bool _nonInteractive;
    private TextWriter? _logWriter;

    public OutputFormatter(bool nonInteractive = false)
    {
        _nonInteractive = nonInteractive;
    }

    /// <summary>
    /// Enable log file mirroring. All console output will also be written to this writer.
    /// </summary>
    public void SetLogWriter(TextWriter writer)
    {
        _logWriter = writer;
    }

    public void Dispose()
    {
        _logWriter?.Flush();
        _logWriter?.Dispose();
        _logWriter = null;
    }

    public void WriteWelcome(string model, SessionInfo? session)
    {
        var provider = session?.Provider ?? "unknown";
        
        if (_nonInteractive)
        {
            if (session is not null)
            {
                Console.WriteLine($"[session:start] id={session.Id} model={model} provider={provider}");
                _logWriter?.WriteLine($"[session:start] id={session.Id} model={model} provider={provider}");
            }
            else
            {
                Console.WriteLine($"[session:start] model={model}");
                _logWriter?.WriteLine($"[session:start] model={model}");
            }
            return;
        }

        WriteColoredLine("╔══════════════════════════════════════╗", ConsoleColor.Cyan);
        WriteColoredLine("║         BotNexus Coding Agent       ║", ConsoleColor.Cyan);
        WriteColoredLine("╚══════════════════════════════════════╝", ConsoleColor.Cyan);
        WriteColoredLine($"Model: {model} | Provider: {provider}", ConsoleColor.DarkCyan);

        if (session is not null)
        {
            WriteSessionInfo(session);
        }
    }

    public void WriteAssistantText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        if (_nonInteractive)
        {
            Console.Write(text);
            _logWriter?.Write(text);
        }
        else
        {
            WriteColored(text, ConsoleColor.Gray);
        }
    }

    public void WriteToolStart(string toolName, string args)
    {
        if (_nonInteractive)
        {
            Console.WriteLine($"[tool:start] {toolName}");
            _logWriter?.WriteLine($"[tool:start] {toolName}");
            return;
        }

        var summary = string.IsNullOrWhiteSpace(args) ? "{}" : args;
        WriteColoredLine($"🔧 {toolName}: {summary}", ConsoleColor.Yellow);
    }

    public void WriteToolEnd(string toolName, bool success)
    {
        if (_nonInteractive)
        {
            Console.WriteLine($"[tool:end] {toolName} success={success.ToString().ToLowerInvariant()}");
            _logWriter?.WriteLine($"[tool:end] {toolName} success={success.ToString().ToLowerInvariant()}");
            return;
        }

        var icon = success ? "✅" : "❌";
        var color = success ? ConsoleColor.Green : ConsoleColor.Red;
        WriteColoredLine($"{icon} {toolName}", color);
    }

    public void WriteError(string message)
    {
        if (_nonInteractive)
        {
            Console.WriteLine($"[error] {message}");
            _logWriter?.WriteLine($"[error] {message}");
        }
        else
        {
            WriteColoredLine(message, ConsoleColor.Red);
        }
    }

    public void WriteTurnSeparator()
    {
        if (_nonInteractive)
        {
            Console.WriteLine("---");
            _logWriter?.WriteLine("---");
        }
        else
        {
            WriteColoredLine(Environment.NewLine + "────────────────────────────────────────", ConsoleColor.DarkGray);
        }
        _logWriter?.Flush();
    }

    public void WriteSessionInfo(SessionInfo session)
    {
        if (_nonInteractive)
        {
            var model = session.Model ?? "unknown";
            var provider = session.Provider ?? "unknown";
            Console.WriteLine($"[session:info] id={session.Id} name={session.Name} model={model} provider={provider} messages={session.MessageCount} updated={session.UpdatedAt:yyyy-MM-dd HH:mm:ss}");
            _logWriter?.WriteLine($"[session:info] id={session.Id} name={session.Name} model={model} provider={provider} messages={session.MessageCount} updated={session.UpdatedAt:yyyy-MM-dd HH:mm:ss}");
        }
        else
        {
            WriteColoredLine(
                $"Session: {session.Id} ({session.Name}) | Messages: {session.MessageCount} | Updated: {session.UpdatedAt:yyyy-MM-dd HH:mm:ss}",
                ConsoleColor.DarkGray);
        }
    }

    private void WriteColored(string text, ConsoleColor color)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.Write(text);
        Console.ForegroundColor = previous;
        _logWriter?.Write(text);
    }

    private void WriteColoredLine(string text, ConsoleColor color)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(text);
        Console.ForegroundColor = previous;
        _logWriter?.WriteLine(text);
    }
}
