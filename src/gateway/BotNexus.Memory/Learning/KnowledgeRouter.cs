namespace BotNexus.Memory.Learning;

/// <summary>
/// Applies routing rules to determine which shared store (if any) an extracted
/// piece of knowledge should be promoted to.
/// </summary>
public sealed class KnowledgeRouter
{
    private readonly IReadOnlyList<KnowledgeRoutingRule> _rules;

    public KnowledgeRouter(IReadOnlyList<KnowledgeRoutingRule> rules)
    {
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
    }

    /// <summary>
    /// Determines the target shared store for a piece of extracted knowledge.
    /// Returns null if no rule matches (knowledge stays in agent-private store).
    /// </summary>
    public string? Route(ExtractedKnowledge knowledge)
    {
        ArgumentNullException.ThrowIfNull(knowledge);

        foreach (var rule in _rules)
        {
            if (rule.Category.HasValue && rule.Category.Value != knowledge.Category)
                continue;

            if (knowledge.Confidence < rule.MinConfidence)
                continue;

            return rule.TargetStore;
        }

        return null;
    }

    /// <summary>
    /// Routes multiple knowledge items and returns them with their resolved target stores.
    /// Items that don't match any rule retain their original TargetStore (null = private).
    /// </summary>
    public IReadOnlyList<ExtractedKnowledge> RouteAll(IEnumerable<ExtractedKnowledge> items)
    {
        var results = new List<ExtractedKnowledge>();
        foreach (var item in items)
        {
            var target = Route(item);
            results.Add(target is not null ? item with { TargetStore = target } : item);
        }

        return results;
    }
}
