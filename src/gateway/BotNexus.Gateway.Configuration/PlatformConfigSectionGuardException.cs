namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Thrown by the raw <see cref="PlatformConfigWriter.MutateAsync(Action{System.Text.Json.Nodes.JsonObject}, string, System.Threading.CancellationToken, IReadOnlyCollection{string})"/>
/// paths when the destructive-section guard refuses a write (issue #2816).
/// </summary>
/// <remarks>
/// <para>
/// The validated write paths (<c>MutateValidatedAsync</c>, <c>MutateSectionAsync</c>) already have
/// an error-list return channel and report a guard rejection through that, exactly like a
/// validation failure, so their callers surface it and exit non-zero unchanged. The raw
/// <c>MutateAsync</c> overloads return <c>Task</c> with no error channel at all, so the rejection
/// has to be thrown: returning quietly would leave the caller believing the write succeeded, which
/// is the exact silent-data-loss failure mode #2816 exists to eliminate.
/// </para>
/// <para>
/// Callers must not catch this to retry the same write. The correct response is either to fix the
/// mutation so it stops destroying an unrelated section, or - when the removal is genuinely
/// intended - to declare that section in <c>namedSections</c> so the intent is explicit in the
/// source.
/// </para>
/// </remarks>
public sealed class PlatformConfigSectionGuardException : InvalidOperationException
{
    /// <summary>Creates the exception with the guard's operator-facing message.</summary>
    public PlatformConfigSectionGuardException(string message)
        : base(message)
    {
    }
}
