namespace BotNexus.Gateway.Abstractions.Conversations;

/// <summary>
/// Represents an optional field in a narrow patch operation. Distinguishes "leave this field
/// untouched" (<see cref="IsSet"/> is <c>false</c>) from "set this field to <see cref="Value"/>"
/// (<see cref="IsSet"/> is <c>true</c>, where the value may itself be <c>null</c> to clear a
/// nullable column).
/// </summary>
/// <remarks>
/// This exists so partial conversation updates carry explicit field ownership: a metadata patch
/// that only changes the title must not implicitly rewrite purpose or instructions, and clearing
/// a nullable field (<c>Value = null</c> with <c>IsSet = true</c>) is unambiguous versus omitting
/// it (<c>IsSet = false</c>). The default value is the "unset" state, so callers can build a patch
/// by assigning only the fields they intend to change.
/// </remarks>
/// <typeparam name="T">The field value type.</typeparam>
public readonly record struct FieldUpdate<T>
{
    private FieldUpdate(T value, bool isSet)
    {
        Value = value;
        IsSet = isSet;
    }

    /// <summary>Gets the value to assign when <see cref="IsSet"/> is <c>true</c>.</summary>
    public T Value { get; }

    /// <summary>Gets a value indicating whether this field should be written by the patch.</summary>
    public bool IsSet { get; }

    /// <summary>Creates a set field carrying the supplied value (which may be <c>null</c> for a nullable T).</summary>
    /// <param name="value">The value to assign.</param>
    public static FieldUpdate<T> Set(T value) => new(value, true);

    /// <summary>Gets the "leave untouched" sentinel (the default state).</summary>
    public static FieldUpdate<T> Unset => default;
}

/// <summary>
/// A narrow patch for editable conversation metadata (title, purpose, instructions). Only fields
/// whose <see cref="FieldUpdate{T}.IsSet"/> is <c>true</c> are written; all other conversation
/// state - bindings, pin, overrides, participants - is left exactly as committed, so a metadata
/// edit cannot revert an independently committed field (issue #2139).
/// </summary>
public sealed record ConversationMetadataPatch
{
    /// <summary>The new title. Title is required, so a set value must be non-null and non-empty.</summary>
    public FieldUpdate<string> Title { get; init; }

    /// <summary>The new purpose, or a set <c>null</c> to clear it.</summary>
    public FieldUpdate<string?> Purpose { get; init; }

    /// <summary>The new instructions, or a set <c>null</c> to clear it.</summary>
    public FieldUpdate<string?> Instructions { get; init; }

    /// <summary>Gets a value indicating whether any field will be written.</summary>
    public bool HasChanges => Title.IsSet || Purpose.IsSet || Instructions.IsSet;
}

/// <summary>
/// A narrow patch for the per-conversation model / thinking / context overrides. Only fields
/// whose <see cref="FieldUpdate{T}.IsSet"/> is <c>true</c> are written; the operation never
/// touches bindings, pin, participants, or metadata, so an override change interleaved with an
/// independent pin/metadata mutation preserves both (issue #2139).
/// </summary>
public sealed record ConversationOverridePatch
{
    /// <summary>The model override id, or a set <c>null</c> to clear it back to the agent default.</summary>
    public FieldUpdate<string?> Model { get; init; }

    /// <summary>The thinking override token, or a set <c>null</c> to clear it.</summary>
    public FieldUpdate<string?> Thinking { get; init; }

    /// <summary>The context-window override, or a set <c>null</c> to clear it.</summary>
    public FieldUpdate<int?> ContextWindow { get; init; }

    /// <summary>Gets a value indicating whether any field will be written.</summary>
    public bool HasChanges => Model.IsSet || Thinking.IsSet || ContextWindow.IsSet;
}
