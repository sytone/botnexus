using System.Text;
using System.Text.RegularExpressions;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using AgentUserMessage = BotNexus.Agent.Core.Types.UserMessage;
using ProviderUserMessage = BotNexus.Agent.Providers.Core.Models.UserMessage;

namespace BotNexus.CodingAgent.Session;

/// <summary>
/// Compacts conversation context when token limits are approached.
/// Keeps recent messages intact and summarizes older ones.
/// </summary>
public sealed class SessionCompactor
{
    private const string SummarizationSystemPrompt =
        "You are a context summarization assistant. Read the conversation and output only a structured summary.";

    private const string SummarizationPrompt = """
        Create a structured context checkpoint summary that another coding model can use to continue work.

        Use this format:
        ## Goal
        ## Constraints & Preferences
        ## Progress
        ### Done
        ### In Progress
        ### Blocked
        ## Key Decisions
        ## Next Steps
        ## Critical Context

        Keep it concise and preserve exact file paths, function names, and errors.
        """;

    public sealed record SessionCompactionOptions(
        int MaxContextTokens = 100000,
        int ReserveTokens = 16384,
        int KeepRecentTokens = 20000,
        int KeepRecentCount = 10,
        LlmClient? LlmClient = null,
        LlmModel? Model = null,
        string? ApiKey = null,
        IReadOnlyDictionary<string, string>? Headers = null,
        string? CustomInstructions = null,
        Func<CompactionHookContext, CancellationToken, Task<string?>>? OnCompactionAsync = null);

    public sealed record CompactionHookContext(
        IReadOnlyList<AgentMessage> MessagesToSummarize,
        IReadOnlyList<AgentMessage> RecentMessages,
        string Summary,
        IReadOnlyList<string> ReadFiles,
        IReadOnlyList<string> ModifiedFiles);

    /// <summary>
    /// Legacy compaction API (count-based).
    /// </summary>
    /// <param name="messages">Current message history.</param>
    /// <param name="keepRecentCount">Number of recent messages to keep intact.</param>
    /// <returns>Compacted message list with summary replacing old messages.</returns>
    public IReadOnlyList<AgentMessage> Compact(
        IReadOnlyList<AgentMessage> messages,
        int keepRecentCount = 10)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (keepRecentCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(keepRecentCount), "keepRecentCount must be >= 0.");
        }

        if (messages.Count <= keepRecentCount)
        {
            return messages;
        }

        var oldMessages = messages.Take(messages.Count - keepRecentCount).ToList();
        var recentMessages = messages.Skip(messages.Count - keepRecentCount).ToList();
        var summaryText = BuildFallbackSummary(oldMessages);
        return BuildCompactedMessages(summaryText, recentMessages);
    }

    /// <summary>
    /// Token-aware compaction API. Uses LLM summarization when context exceeds threshold.
    /// </summary>
    public async Task<IReadOnlyList<AgentMessage>> CompactAsync(
        IReadOnlyList<AgentMessage> messages,
        SessionCompactionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(options);
        if (messages.Count == 0)
        {
            return messages;
        }

        var contextTokens = EstimateTokens(messages);
        var threshold = Math.Max(1, options.MaxContextTokens - options.ReserveTokens);
        if (contextTokens <= threshold)
        {
            return messages;
        }

        var cutIndex = FindCutIndex(messages, options);
        if (cutIndex <= 0 || cutIndex >= messages.Count)
        {
            return messages;
        }

        var splitTurnStart = FindTurnStartIndex(messages, cutIndex);
        var isSplitTurn = splitTurnStart >= 0 && splitTurnStart < cutIndex;
        var summaryCutIndex = isSplitTurn ? splitTurnStart : cutIndex;
        var oldMessages = messages.Take(summaryCutIndex).ToList();
        var splitTurnPrefix = isSplitTurn
            ? messages.Skip(splitTurnStart).Take(cutIndex - splitTurnStart).ToList()
            : [];
        var recentMessages = messages.Skip(cutIndex).ToList();
        var fileOps = ExtractFileOperations(oldMessages);
        var llmSummary = await TryGenerateSummaryAsync(oldMessages, options, cancellationToken).ConfigureAwait(false);
        var summaryText = string.IsNullOrWhiteSpace(llmSummary)
            ? BuildFallbackSummary(oldMessages)
            : llmSummary.Trim();

        if (isSplitTurn && splitTurnPrefix.Count > 0)
        {
            summaryText = $"{summaryText}{Environment.NewLine}{Environment.NewLine}---{Environment.NewLine}{Environment.NewLine}**Turn Context (split turn):**{Environment.NewLine}{Environment.NewLine}{BuildTurnPrefixSummary(splitTurnPrefix)}";
        }

        summaryText = AppendFileOperations(summaryText, fileOps.ReadFiles, fileOps.ModifiedFiles);

        if (options.OnCompactionAsync is not null)
        {
            var overrideSummary = await options.OnCompactionAsync(
                    new CompactionHookContext(oldMessages, recentMessages, summaryText, fileOps.ReadFiles, fileOps.ModifiedFiles),
                    cancellationToken)
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(overrideSummary))
            {
                summaryText = overrideSummary.Trim();
            }
        }

        return BuildCompactedMessages(summaryText, recentMessages);
    }

    public int EstimateTokens(IReadOnlyList<AgentMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        return messages.Sum(EstimateTokens);
    }

    private static int FindCutIndex(IReadOnlyList<AgentMessage> messages, SessionCompactionOptions options)
    {
        var minRecent = Math.Min(Math.Max(1, options.KeepRecentCount), messages.Count);
        var minCut = Math.Max(0, messages.Count - minRecent);
        var runningRecentTokens = 0;
        var cutIndex = minCut;

        for (var i = messages.Count - 1; i >= 0; i--)
        {
            runningRecentTokens += EstimateTokens(messages[i]);
            var recentCount = messages.Count - i;
            if (runningRecentTokens >= options.KeepRecentTokens && recentCount >= minRecent)
            {
                cutIndex = i;
                break;
            }
        }

        return FindNextValidCutIndex(messages, cutIndex);
    }

    private static int FindNextValidCutIndex(IReadOnlyList<AgentMessage> messages, int startIndex)
    {
        var firstValidBoundary = -1;
        for (var index = Math.Max(1, startIndex); index < messages.Count; index++)
        {
            if (messages[index] is ToolResultAgentMessage)
            {
                continue;
            }

            if (firstValidBoundary < 0)
            {
                firstValidBoundary = index;
            }

            if (messages[index] is AgentUserMessage)
            {
                return index;
            }
        }

        if (firstValidBoundary >= 0)
        {
            return firstValidBoundary;
        }

        return messages.Count;
    }

    private static int FindTurnStartIndex(IReadOnlyList<AgentMessage> messages, int cutIndex)
    {
        if (cutIndex < 0 || cutIndex >= messages.Count)
        {
            return -1;
        }

        if (messages[cutIndex] is AgentUserMessage)
        {
            return -1;
        }

        for (var index = cutIndex - 1; index >= 0; index--)
        {
            if (messages[index] is AgentUserMessage)
            {
                return index;
            }
        }

        return -1;
    }

    private static int EstimateTokens(AgentMessage message)
    {
        if (message is AssistantAgentMessage { Usage: not null } assistantWithUsage)
        {
            var usageTokens = (assistantWithUsage.Usage!.InputTokens ?? 0) + (assistantWithUsage.Usage.OutputTokens ?? 0);
            if (usageTokens > 0)
            {
                return usageTokens;
            }
        }

        var chars = message switch
        {
            AgentUserMessage user => user.Content?.Length ?? 0,
            AssistantAgentMessage assistant => (assistant.Content?.Length ?? 0) + EstimateToolCallChars(assistant),
            SystemAgentMessage system => system.Content?.Length ?? 0,
            ToolResultAgentMessage tool => tool.Result.Content.Sum(static content => content.Value.Length),
            _ => 0
        };

        return Math.Max(1, (int)Math.Ceiling(chars / 4d));
    }

    private static int EstimateToolCallChars(AssistantAgentMessage assistant)
    {
        if (assistant.ToolCalls is null || assistant.ToolCalls.Count == 0)
        {
            return 0;
        }

        return assistant.ToolCalls.Sum(static call =>
            call.Name.Length + call.Id.Length + call.Arguments.Sum(argument => argument.Key.Length + (argument.Value?.ToString()?.Length ?? 0)));
    }

    private static IReadOnlyList<AgentMessage> BuildCompactedMessages(string summaryText, IReadOnlyList<AgentMessage> recentMessages)
    {
        var compacted = new List<AgentMessage>(1 + recentMessages.Count)
        {
            new SystemAgentMessage(summaryText)
        };
        compacted.AddRange(recentMessages);
        return compacted;
    }

    private static string BuildFallbackSummary(IReadOnlyList<AgentMessage> messages)
    {
        var keyTopics = ExtractKeyTopics(messages);
        var fileOps = ExtractFileOperations(messages);
        var decisionsMade = ExtractDecisions(messages);

        return
            $"[Session context summary: {messages.Count} earlier messages compacted.{Environment.NewLine}" +
            $" Key topics discussed: {string.Join(", ", keyTopics)}{Environment.NewLine}" +
            $" Files modified: {string.Join(", ", fileOps.ModifiedFiles.DefaultIfEmpty("none identified"))}{Environment.NewLine}" +
            $" Decisions made: {string.Join("; ", decisionsMade)}]";
    }

    private static string AppendFileOperations(string summary, IReadOnlyList<string> readFiles, IReadOnlyList<string> modifiedFiles)
    {
        var builder = new StringBuilder(summary.TrimEnd());
        if (readFiles.Count > 0)
        {
            builder.AppendLine()
                .AppendLine()
                .AppendLine("<read-files>")
                .AppendLine(string.Join(Environment.NewLine, readFiles))
                .AppendLine("</read-files>");
        }

        if (modifiedFiles.Count > 0)
        {
            builder.AppendLine()
                .AppendLine("<modified-files>")
                .AppendLine(string.Join(Environment.NewLine, modifiedFiles))
                .AppendLine("</modified-files>");
        }

        return builder.ToString();
    }

    private static string BuildTurnPrefixSummary(IReadOnlyList<AgentMessage> turnPrefixMessages)
    {
        var originalRequest = turnPrefixMessages
            .OfType<AgentUserMessage>()
            .Select(message => message.Content)
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text))
            ?? "(request unavailable)";

        var progress = turnPrefixMessages
            .OfType<AssistantAgentMessage>()
            .Select(message => message.Content)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Take(2)
            .ToList();
        var toolCalls = turnPrefixMessages
            .OfType<AssistantAgentMessage>()
            .SelectMany(message => message.ToolCalls ?? [])
            .Select(call => $"{call.Name}({string.Join(", ", call.Arguments.Keys)})")
            .Take(3)
            .ToList();

        if (toolCalls.Count > 0)
        {
            progress.Add($"Tool calls: {string.Join("; ", toolCalls)}");
        }

        if (progress.Count == 0)
        {
            progress.Add("(none)");
        }

        return
            $"## Original Request{Environment.NewLine}{originalRequest}{Environment.NewLine}{Environment.NewLine}" +
            $"## Early Progress{Environment.NewLine}- {string.Join($"{Environment.NewLine}- ", progress)}{Environment.NewLine}{Environment.NewLine}" +
            $"## Context for Suffix{Environment.NewLine}- Use this request context to interpret retained tool results and assistant follow-ups.";
    }

    private async Task<string?> TryGenerateSummaryAsync(
        IReadOnlyList<AgentMessage> messages,
        SessionCompactionOptions options,
        CancellationToken cancellationToken)
    {
        if (options.Model is null)
        {
            return null;
        }

        try
        {
            var prompt = BuildSummarizationPrompt(messages, options.CustomInstructions);
            var context = new Context(
                SystemPrompt: SummarizationSystemPrompt,
                Messages:
                [
                    new ProviderUserMessage(
                        new UserMessageContent(prompt),
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                ]);

            if (options.LlmClient is null)
            {
                return null;
            }

            var completion = await options.LlmClient.CompleteSimpleAsync(
                    options.Model,
                    context,
                    new SimpleStreamOptions
                    {
                        ApiKey = options.ApiKey,
                        Headers = options.Headers is null ? null : new Dictionary<string, string>(options.Headers),
                        MaxTokens = Math.Max(512, (int)Math.Floor(options.ReserveTokens * 0.8))
                    })
                .ConfigureAwait(false);

            return string.Join(
                Environment.NewLine,
                completion.Content.OfType<TextContent>().Select(static content => content.Text).Where(static text => !string.IsNullOrWhiteSpace(text)));
        }
        catch
        {
            return null;
        }
    }

    private static string BuildSummarizationPrompt(IReadOnlyList<AgentMessage> messages, string? customInstructions)
    {
        var conversation = SerializeConversation(messages);
        var builder = new StringBuilder()
            .AppendLine("<conversation>")
            .AppendLine(conversation)
            .AppendLine("</conversation>")
            .AppendLine()
            .AppendLine(SummarizationPrompt);

        if (!string.IsNullOrWhiteSpace(customInstructions))
        {
            builder.AppendLine()
                .AppendLine("Additional focus:")
                .AppendLine(customInstructions.Trim());
        }

        return builder.ToString();
    }

    private static string SerializeConversation(IReadOnlyList<AgentMessage> messages)
    {
        var lines = new List<string>(messages.Count);
        foreach (var message in messages)
        {
            switch (message)
            {
                case AgentUserMessage user:
                    lines.Add($"[User]: {user.Content}");
                    break;
                case AssistantAgentMessage assistant:
                    if (!string.IsNullOrWhiteSpace(assistant.Content))
                    {
                        lines.Add($"[Assistant]: {assistant.Content}");
                    }

                    if (assistant.ToolCalls is { Count: > 0 })
                    {
                        var calls = assistant.ToolCalls.Select(call =>
                            $"{call.Name}({string.Join(", ", call.Arguments.Select(pair => $"{pair.Key}={pair.Value}"))})");
                        lines.Add($"[Assistant tool calls]: {string.Join("; ", calls)}");
                    }

                    break;
                case ToolResultAgentMessage toolResult:
                {
                    var text = string.Join(Environment.NewLine, toolResult.Result.Content.Select(content => content.Value));
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        lines.Add($"[Tool result]: {text}");
                    }

                    break;
                }
                case SystemAgentMessage system:
                    if (!string.IsNullOrWhiteSpace(system.Content))
                    {
                        lines.Add($"[System]: {system.Content}");
                    }

                    break;
            }
        }

        return string.Join(Environment.NewLine + Environment.NewLine, lines);
    }

    private static IReadOnlyList<string> ExtractKeyTopics(IReadOnlyList<AgentMessage> messages)
    {
        var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the", "and", "for", "that", "with", "this", "from", "have", "will", "were", "into", "tool", "message", "messages", "file"
        };

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var text in ExtractText(messages))
        {
            foreach (var word in Regex.Matches(text, "[A-Za-z][A-Za-z0-9_-]{3,}").Select(match => match.Value))
            {
                if (stopWords.Contains(word))
                {
                    continue;
                }

                counts[word] = counts.TryGetValue(word, out var current) ? current + 1 : 1;
            }
        }

        return counts.Count == 0
            ? ["none identified"]
            : counts.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Take(5).Select(pair => pair.Key).ToList();
    }

    private sealed record FileOperations(IReadOnlyList<string> ReadFiles, IReadOnlyList<string> ModifiedFiles);

    private static FileOperations ExtractFileOperations(IReadOnlyList<AgentMessage> messages)
    {
        var readFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var modifiedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var assistant in messages.OfType<AssistantAgentMessage>())
        {
            if (assistant.ToolCalls is null)
            {
                continue;
            }

            foreach (var toolCall in assistant.ToolCalls)
            {
                if (!toolCall.Arguments.TryGetValue("path", out var pathValue))
                {
                    continue;
                }

                if (pathValue?.ToString() is not { Length: > 0 } rawPath)
                {
                    continue;
                }

                var normalizedPath = rawPath.Replace('/', Path.DirectorySeparatorChar);
                if (toolCall.Name.Equals("read", StringComparison.OrdinalIgnoreCase))
                {
                    readFiles.Add(normalizedPath);
                }
                else if (toolCall.Name.Equals("write", StringComparison.OrdinalIgnoreCase) ||
                         toolCall.Name.Equals("edit", StringComparison.OrdinalIgnoreCase))
                {
                    modifiedFiles.Add(normalizedPath);
                }
            }
        }

        foreach (var toolResult in messages.OfType<ToolResultAgentMessage>())
        {
            foreach (var content in toolResult.Result.Content)
            {
                foreach (Match match in Regex.Matches(content.Value, @"(?<!\w)([A-Za-z0-9_\-./\\]+?\.[A-Za-z0-9]{1,8})(?::\d+)?"))
                {
                    var normalizedPath = match.Groups[1].Value.Replace('/', Path.DirectorySeparatorChar);
                    if (toolResult.ToolName.Equals("read", StringComparison.OrdinalIgnoreCase))
                    {
                        readFiles.Add(normalizedPath);
                    }
                    else
                    {
                        modifiedFiles.Add(normalizedPath);
                    }
                }
            }
        }

        var readOnlyFiles = readFiles.Where(path => !modifiedFiles.Contains(path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Take(25)
            .ToList();
        var modified = modifiedFiles.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Take(25)
            .ToList();
        return new FileOperations(readOnlyFiles, modified);
    }

    private static IReadOnlyList<string> ExtractDecisions(IReadOnlyList<AgentMessage> messages)
    {
        var decisions = new List<string>();
        foreach (var text in ExtractText(messages))
        {
            if (!Regex.IsMatch(text, @"\b(decide|decision|should|will|use|implement|add|fix)\b", RegexOptions.IgnoreCase))
            {
                continue;
            }

            var cleaned = Regex.Replace(text, @"\s+", " ").Trim();
            if (!string.IsNullOrWhiteSpace(cleaned))
            {
                decisions.Add(cleaned.Length > 120 ? $"{cleaned[..117]}..." : cleaned);
            }

            if (decisions.Count >= 3)
            {
                break;
            }
        }

        return decisions.Count == 0 ? ["none identified"] : decisions;
    }

    private static IEnumerable<string> ExtractText(IReadOnlyList<AgentMessage> messages)
    {
        foreach (var message in messages)
        {
            switch (message)
            {
                case AgentUserMessage user:
                    if (!string.IsNullOrWhiteSpace(user.Content))
                    {
                        yield return user.Content;
                    }

                    break;
                case AssistantAgentMessage assistant:
                    if (!string.IsNullOrWhiteSpace(assistant.Content))
                    {
                        yield return assistant.Content;
                    }

                    break;
                case SystemAgentMessage system:
                    if (!string.IsNullOrWhiteSpace(system.Content))
                    {
                        yield return system.Content;
                    }

                    break;
                case ToolResultAgentMessage tool:
                    foreach (var content in tool.Result.Content)
                    {
                        if (!string.IsNullOrWhiteSpace(content.Value))
                        {
                            yield return content.Value;
                        }
                    }

                    break;
            }
        }
    }
}
