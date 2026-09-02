namespace BotNexus.Agent.Providers.Core.Validation;

/// <summary>
/// Ambient session facts the validator may use to make a rejection actionable, supplied by the
/// caller because the validator itself is a pure static function over (arguments, schema).
/// </summary>
/// <param name="MostRecentlyReadPath">
/// The file path most recently returned by a <c>read</c> in this session, when one is known.
/// </param>
/// <remarks>
/// Issue #3711. <c>edit</c> called with a complete <c>edits</c> array and no <c>path</c> was
/// rejected ~20 times a week, discarding a fully-formed multi-line payload each time. The tool
/// already tracks per-session read state, so the probable target is knowable — but only to the
/// caller, not to a static validator. This record is that seam.
/// <para>
/// It carries a <b>suggestion</b> and nothing more. The validator never writes this path into
/// the arguments: silently retargeting an edit at a file the caller did not name is a
/// destructive write, and a wrong guess would corrupt a file with no failure signal at all.
/// </para>
/// </remarks>
public sealed record ToolCallValidationContext(string? MostRecentlyReadPath = null);
