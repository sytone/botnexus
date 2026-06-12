using System.Text.RegularExpressions;

namespace BotNexus.Memory.Learning;

/// <summary>
/// Heuristic classifier that determines whether a conversation turn pair
/// contains durable (reusable) knowledge or is transient small-talk.
/// Uses keyword detection, length thresholds, and structural patterns.
/// </summary>
public static class TurnClassifier
{
    private const int MinContentLengthForDurable = 100;
    private const int StrongContentLengthThreshold = 300;

    private static readonly (Regex Pattern, KnowledgeCategory Category, double Weight)[] CategoryPatterns =
    [
        // Decisions
        (new Regex(@"\b(decided|decision|chose|choosing|went with|settled on|agreed|approved)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), KnowledgeCategory.Decision, 0.4),
        (new Regex(@"\b(we('ll| will| should)|going (to|with)|the plan is)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), KnowledgeCategory.Decision, 0.3),

        // Patterns
        (new Regex(@"\b(pattern|convention|always|never|rule|standard|best practice|guideline)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), KnowledgeCategory.Pattern, 0.4),
        (new Regex(@"\b(whenever|every time|consistently|typical(ly)?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), KnowledgeCategory.Pattern, 0.3),

        // Facts
        (new Regex(@"\b(is located at|lives in|uses|version|configured|installed|runs on|deployed)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), KnowledgeCategory.Fact, 0.3),
        (new Regex(@"\b(the (path|url|endpoint|port|address) is|found at)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), KnowledgeCategory.Fact, 0.4),

        // Procedures
        (new Regex(@"\b(step \d|first[,.]|then[,.]|finally[,.]|to do this|how to|instructions|workflow)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), KnowledgeCategory.Procedure, 0.4),
        (new Regex(@"\b(run|execute|invoke|call|use the command)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), KnowledgeCategory.Procedure, 0.2),

        // Preferences
        (new Regex(@"\b(prefer|like|don't like|rather|favorite|avoid|instead of)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), KnowledgeCategory.Preference, 0.4),
        (new Regex(@"\b(style|format|tone|approach|way I)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), KnowledgeCategory.Preference, 0.3),
    ];

    private static readonly Regex TransientPatterns = new(
        @"\b(hello|hi|hey|thanks|thank you|ok|okay|sure|got it|sounds good|no problem|bye|goodbye|good morning|good night)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex QuestionOnlyPattern = new(
        @"^\s*\??\s*$|^[^.!]*\?\s*$",
        RegexOptions.Compiled);

    /// <summary>
    /// Classifies a user+assistant turn pair as transient or durable.
    /// </summary>
    /// <param name="userContent">The user's message content.</param>
    /// <param name="assistantContent">The assistant's response content.</param>
    /// <returns>Classification result with durability, category, and confidence.</returns>
    public static ClassificationResult Classify(string userContent, string assistantContent)
    {
        ArgumentNullException.ThrowIfNull(userContent);
        ArgumentNullException.ThrowIfNull(assistantContent);

        var combinedContent = $"{userContent}\n{assistantContent}";
        var totalLength = combinedContent.Length;

        // Short exchanges are almost always transient
        if (totalLength < MinContentLengthForDurable)
        {
            return new ClassificationResult(false, null, 0.9);
        }

        // Pure greetings/acknowledgements are transient
        if (IsTransientExchange(userContent, assistantContent))
        {
            return new ClassificationResult(false, null, 0.85);
        }

        // Score each category
        var categoryScores = new Dictionary<KnowledgeCategory, double>();
        foreach (var (pattern, category, weight) in CategoryPatterns)
        {
            var matches = pattern.Matches(combinedContent);
            if (matches.Count > 0)
            {
                var score = Math.Min(1.0, matches.Count * weight);
                if (!categoryScores.TryGetValue(category, out var existing) || score > existing)
                {
                    categoryScores[category] = score;
                }
            }
        }

        if (categoryScores.Count == 0)
        {
            // No category signals but content is long enough — might still be durable
            if (totalLength >= StrongContentLengthThreshold)
            {
                return new ClassificationResult(true, null, 0.4);
            }

            return new ClassificationResult(false, null, 0.6);
        }

        // Pick highest-scoring category
        var bestCategory = categoryScores.MaxBy(kvp => kvp.Value);
        var confidence = Math.Min(0.95, bestCategory.Value + (totalLength >= StrongContentLengthThreshold ? 0.2 : 0.0));

        return new ClassificationResult(true, bestCategory.Key, confidence);
    }

    private static bool IsTransientExchange(string userContent, string assistantContent)
    {
        // If both user and assistant messages are short greetings/acks
        var userIsTransient = TransientPatterns.IsMatch(userContent) && userContent.Length < 50;
        var assistantIsTransient = TransientPatterns.IsMatch(assistantContent) && assistantContent.Length < 80;

        return userIsTransient && assistantIsTransient;
    }
}
