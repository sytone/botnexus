using Vogen;

namespace BotNexus.Domain.Primitives;

/// <summary>
/// Names a tool an agent may invoke - the identifier the model emits in a tool call and the
/// executor dispatches on. Construct via <see cref="From(string)"/>; the value must be non-null,
/// non-empty and non-whitespace, and is stored trimmed and lower-cased.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why lower-cased.</b> Tool dispatch throughout the agent loop is case-insensitive - the
/// per-turn tool set in <c>AgentLoopRunner</c> is an <see cref="StringComparer.OrdinalIgnoreCase"/>
/// set, and the executor matches registered tools the same way. The previous hand-rolled struct
/// expressed that by overriding <c>Equals</c>/<c>GetHashCode</c> with an ordinal-ignore-case
/// comparer while preserving the caller's casing in <c>Value</c>. Vogen generates equality from the
/// underlying primitive and does not accept a hand-written <c>Equals</c>, so the same contract is
/// preserved by canonicalising the value instead: <c>ToolName.From("Read_File")</c> and
/// <c>ToolName.From("read_file")</c> remain equal. This follows the existing
/// <see cref="ChannelKey"/> precedent, which canonicalises the same way for the same reason.
/// </para>
/// <para>
/// Migrated from a hand-rolled <c>readonly record struct</c> to Vogen in #502 (primitive obsession
/// phase 3) so construction, validation, JSON and equality are generated rather than duplicated.
/// The wire representation is unchanged - a bare JSON string, exactly as the retired
/// <c>ToolNameJsonConverter</c> emitted.
/// </para>
/// </remarks>
[ValueObject<string>(conversions: Conversions.SystemTextJson)]
public readonly partial struct ToolName
{
    private static Validation Validate(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? Validation.Invalid("ToolName cannot be null, empty, or whitespace.")
            : Validation.Ok;

    private static string NormalizeInput(string input) =>
        input is null ? input! : input.Trim().ToLowerInvariant();
}
