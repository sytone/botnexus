namespace BotNexus.Memory.Models;

/// <summary>Explains whether a revision-aware mutation committed or why it did not.</summary>
public enum MemoryMutationStatus
{
    /// <summary>The mutation committed atomically.</summary>
    Applied,
    /// <summary>No record existed for the supplied identifier.</summary>
    NotFound,
    /// <summary>The record existed but no longer had the caller's expected revision.</summary>
    RevisionConflict,
    /// <summary>The archive request targeted a record that was already archived.</summary>
    AlreadyArchived
}

/// <summary>Typed result for optimistic memory mutations; non-applied results include the current record when it exists.</summary>
public sealed record MemoryMutationResult(MemoryMutationStatus Status, MemoryEntry? Entry);

/// <summary>Distinguishes an omitted update field from an explicit replacement, including replacement with null.</summary>
public readonly record struct MemoryUpdateValue<T>
{
    private MemoryUpdateValue(T value)
    {
        IsSpecified = true;
        Value = value;
    }

    /// <summary>Whether the caller supplied this field.</summary>
    public bool IsSpecified { get; }

    /// <summary>The supplied replacement, which may itself be null.</summary>
    public T? Value { get; }

    /// <summary>Creates an explicit replacement, including an explicit null for nullable fields.</summary>
    public static MemoryUpdateValue<T> Replace(T value) => new(value);

    /// <summary>Allows ordinary non-null assignments while default remains the omitted state.</summary>
    public static implicit operator MemoryUpdateValue<T>(T value) => Replace(value);
}

/// <summary>Fields callers may replace in one revision-aware update.</summary>
public sealed record MemoryUpdate
{
    /// <summary>Replacement content; null preserves the current content.</summary>
    public string? Content { get; init; }
    /// <summary>Optional role replacement; omitted preserves and an explicit null clears.</summary>
    public MemoryUpdateValue<string?> Role { get; init; }
    /// <summary>Optional category replacement; omitted preserves and an explicit null clears.</summary>
    public MemoryUpdateValue<string?> Category { get; init; }
    /// <summary>Optional canonical tag JSON replacement; omitted preserves and an explicit null clears.</summary>
    public MemoryUpdateValue<string?> TagsJson { get; init; }
    /// <summary>Optional correction target replacement; omitted preserves and an explicit null clears.</summary>
    public MemoryUpdateValue<string?> CorrectsId { get; init; }
    /// <summary>Optional superseded-record replacement; omitted preserves and an explicit null clears.</summary>
    public MemoryUpdateValue<string?> SupersedesId { get; init; }
    /// <summary>Optional superseding-record replacement; omitted preserves and an explicit null clears.</summary>
    public MemoryUpdateValue<string?> SupersededById { get; init; }
    /// <summary>Optional embedding-state replacement; omitted preserves and an explicit null clears.</summary>
    public MemoryUpdateValue<string?> EmbeddingStatus { get; init; }
}
