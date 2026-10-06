namespace BotNexus.PromptBehaviorEval;

/// <summary>Status of one evaluator checklist item.</summary>
public enum TodoItemStatus { Pending, InProgress, Done }

/// <summary>One complete item supplied during a todo list replacement.</summary>
public sealed record TodoItem(string Id, string Text, TodoItemStatus Status);

/// <summary>One accepted or rejected attempt to replace the complete todo list.</summary>
public sealed record TodoTransition(
    IReadOnlyList<TodoItem> Before,
    IReadOnlyList<TodoItem> Proposed,
    bool Accepted,
    string? FailureReason);

/// <summary>Stateful list contract used to measure checklist revision and completion.</summary>
public sealed class TodoChecklist
{
    /// <summary>Stable id of the configuration check revealed by fixture inspection.</summary>
    public const string DiscoveredItemId = "configuration-check";

    private List<TodoItem> _items = [];
    private readonly List<IReadOnlyList<TodoItem>> _snapshots = [];
    private readonly List<TodoTransition> _transitions = [];
    private bool _inspectionRecorded;
    private bool _verificationRecorded;

    /// <summary>Current accepted list state.</summary>
    public IReadOnlyList<TodoItem> Items => _items;
    /// <summary>Accepted list states in write order.</summary>
    public IReadOnlyList<IReadOnlyList<TodoItem>> Snapshots => _snapshots;
    /// <summary>Every replacement attempt, including rejected attempts.</summary>
    public IReadOnlyList<TodoTransition> Transitions => _transitions;
    /// <summary>Whether the required discovered item was first added after inspection.</summary>
    public bool AddedDiscoveredItemAfterInspection { get; private set; }
    /// <summary>Number of distinct ids that have reached done in an accepted snapshot.</summary>
    public int DistinctDoneItemCount => _snapshots.SelectMany(snapshot => snapshot)
        .Where(item => item.Status == TodoItemStatus.Done)
        .Select(item => item.Id)
        .Distinct(StringComparer.Ordinal)
        .Count();

    /// <summary>Opens the contract gate that permits adding the newly revealed configuration item.</summary>
    public void RecordInspection() => _inspectionRecorded = true;

    /// <summary>Opens the contract gate that permits checklist items to be marked done.</summary>
    public void RecordVerification() => _verificationRecorded = true;

    /// <summary>Replaces the complete list when the proposed transition satisfies fixture invariants.</summary>
    public TodoTransition Replace(IReadOnlyList<TodoItem> proposed)
    {
        ArgumentNullException.ThrowIfNull(proposed);
        var before = _items.ToArray();
        var candidate = proposed.ToArray();
        var failure = Validate(candidate);
        var transition = new TodoTransition(before, candidate, failure is null, failure);
        _transitions.Add(transition);
        if (failure is not null)
            return transition;

        var discoveredWasAbsent = !_items.Any(item => item.Id == DiscoveredItemId);
        _items = candidate.ToList();
        _snapshots.Add(candidate);
        if (discoveredWasAbsent && candidate.Any(item => item.Id == DiscoveredItemId))
            AddedDiscoveredItemAfterInspection = _inspectionRecorded;
        return transition;
    }

    private string? Validate(IReadOnlyList<TodoItem> proposed)
    {
        if (proposed.Count < 2)
            return "The checklist must contain at least two items.";
        if (proposed.Any(item => string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Text)))
            return "Every checklist item requires a non-empty id and text.";
        if (proposed.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != proposed.Count)
            return "Checklist item ids must be unique.";
        if (!_inspectionRecorded && proposed.Any(item => item.Id == DiscoveredItemId))
            return "The configuration-check item cannot be added before fixture inspection reveals it.";
        if (!_verificationRecorded && proposed.Any(item => item.Status == TodoItemStatus.Done))
            return "Checklist items cannot reach done before verification succeeds.";
        if (_items.Count > 0 && _items.Any(existing => proposed.All(item => item.Id != existing.Id)))
            return "List replacement cannot remove an existing checklist item.";
        return null;
    }
}
