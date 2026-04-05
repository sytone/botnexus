using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BotNexus.AgentCore.Types;

namespace BotNexus.CodingAgent.Session;

public sealed class SessionManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public Task<SessionInfo> CreateSessionAsync(string workingDir, string? name)
    {
        var root = GetSessionsRoot(workingDir);
        var id = GenerateSessionId();
        var now = DateTimeOffset.UtcNow;

        var session = new SessionInfo(
            Id: id,
            Name: string.IsNullOrWhiteSpace(name) ? id : name.Trim(),
            CreatedAt: now,
            UpdatedAt: now,
            MessageCount: 0,
            Model: null,
            WorkingDirectory: Path.GetFullPath(workingDir));

        var sessionDirectory = Path.Combine(root, id);
        Directory.CreateDirectory(sessionDirectory);
        WriteSessionMetadata(sessionDirectory, session);
        File.WriteAllText(Path.Combine(sessionDirectory, "messages.jsonl"), string.Empty);
        return Task.FromResult(session);
    }

    public async Task SaveSessionAsync(SessionInfo session, IReadOnlyList<AgentMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(messages);

        var root = GetSessionsRoot(session.WorkingDirectory);
        var sessionDirectory = Path.Combine(root, session.Id);
        if (!Directory.Exists(sessionDirectory))
        {
            Directory.CreateDirectory(sessionDirectory);
        }

        var updated = session with
        {
            UpdatedAt = DateTimeOffset.UtcNow,
            MessageCount = messages.Count
        };

        WriteSessionMetadata(sessionDirectory, updated);

        var messagesPath = Path.Combine(sessionDirectory, "messages.jsonl");
        await using var stream = new FileStream(messagesPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        foreach (var message in messages)
        {
            var envelope = SerializeMessage(message);
            var line = JsonSerializer.Serialize(envelope, JsonOptions);
            await writer.WriteLineAsync(line).ConfigureAwait(false);
        }
    }

    public async Task<(SessionInfo Session, IReadOnlyList<AgentMessage> Messages)> ResumeSessionAsync(string sessionId, string workingDir)
    {
        var root = GetSessionsRoot(workingDir);
        var sessionDirectory = Path.Combine(root, sessionId);
        var metadataPath = Path.Combine(sessionDirectory, "session.json");
        var messagesPath = Path.Combine(sessionDirectory, "messages.jsonl");

        if (!File.Exists(metadataPath))
        {
            throw new FileNotFoundException($"Session '{sessionId}' does not exist.", metadataPath);
        }

        var metadataJson = await File.ReadAllTextAsync(metadataPath).ConfigureAwait(false);
        var session = JsonSerializer.Deserialize<SessionInfo>(metadataJson, JsonOptions)
            ?? throw new InvalidOperationException($"Session metadata is invalid for '{sessionId}'.");

        var messages = new List<AgentMessage>();
        if (File.Exists(messagesPath))
        {
            var lines = await File.ReadAllLinesAsync(messagesPath).ConfigureAwait(false);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var envelope = JsonSerializer.Deserialize<MessageEnvelope>(line, JsonOptions)
                    ?? throw new InvalidOperationException($"Invalid message entry in session '{sessionId}'.");
                messages.Add(DeserializeMessage(envelope));
            }
        }

        return (session, messages);
    }

    public async Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(string workingDir)
    {
        var root = GetSessionsRoot(workingDir);
        if (!Directory.Exists(root))
        {
            return [];
        }

        var sessions = new List<SessionInfo>();
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var metadataPath = Path.Combine(directory, "session.json");
            if (!File.Exists(metadataPath))
            {
                continue;
            }

            var json = await File.ReadAllTextAsync(metadataPath).ConfigureAwait(false);
            var session = JsonSerializer.Deserialize<SessionInfo>(json, JsonOptions);
            if (session is not null)
            {
                sessions.Add(session);
            }
        }

        return sessions
            .OrderByDescending(session => session.UpdatedAt)
            .ToList();
    }

    public Task DeleteSessionAsync(string sessionId, string workingDir)
    {
        var root = GetSessionsRoot(workingDir);
        var sessionDirectory = Path.Combine(root, sessionId);
        if (Directory.Exists(sessionDirectory))
        {
            Directory.Delete(sessionDirectory, recursive: true);
        }

        return Task.CompletedTask;
    }

    private static void WriteSessionMetadata(string sessionDirectory, SessionInfo session)
    {
        var metadataPath = Path.Combine(sessionDirectory, "session.json");
        var json = JsonSerializer.Serialize(session, JsonOptions);
        File.WriteAllText(metadataPath, json);
    }

    private static string GetSessionsRoot(string workingDir)
    {
        var config = CodingAgentConfig.Load(workingDir);
        Directory.CreateDirectory(config.SessionsDirectory);
        return config.SessionsDirectory;
    }

    private static string GenerateSessionId()
    {
        var now = DateTime.UtcNow;
        var bytes = RandomNumberGenerator.GetBytes(2);
        var suffix = Convert.ToHexString(bytes).ToLowerInvariant();
        return $"{now:yyyyMMdd-HHmmss}-{suffix}";
    }

    private static MessageEnvelope SerializeMessage(AgentMessage message)
    {
        return message switch
        {
            UserMessage user => new MessageEnvelope("user", JsonSerializer.SerializeToElement(user, JsonOptions)),
            AssistantAgentMessage assistant => new MessageEnvelope("assistant", JsonSerializer.SerializeToElement(assistant, JsonOptions)),
            ToolResultAgentMessage tool => new MessageEnvelope("tool", JsonSerializer.SerializeToElement(tool, JsonOptions)),
            SystemAgentMessage system => new MessageEnvelope("system", JsonSerializer.SerializeToElement(system, JsonOptions)),
            _ => throw new NotSupportedException($"Unsupported message type: {message.GetType().Name}")
        };
    }

    private static AgentMessage DeserializeMessage(MessageEnvelope envelope)
    {
        return envelope.Type switch
        {
            "user" => envelope.Payload.Deserialize<UserMessage>(JsonOptions)
                      ?? throw new InvalidOperationException("Invalid user message payload."),
            "assistant" => envelope.Payload.Deserialize<AssistantAgentMessage>(JsonOptions)
                           ?? throw new InvalidOperationException("Invalid assistant message payload."),
            "tool" => envelope.Payload.Deserialize<ToolResultAgentMessage>(JsonOptions)
                      ?? throw new InvalidOperationException("Invalid tool message payload."),
            "system" => envelope.Payload.Deserialize<SystemAgentMessage>(JsonOptions)
                        ?? throw new InvalidOperationException("Invalid system message payload."),
            _ => throw new NotSupportedException($"Unsupported message type '{envelope.Type}'.")
        };
    }

    private sealed record MessageEnvelope(string Type, JsonElement Payload);
}
