using System.Text;

namespace BotNexus.Extensions.Channels.Matrix;

/// <summary>
/// A Matrix access token that refuses to render its own value when formatted, so it cannot reach a
/// log sink, an exception message, or a serialiser by accident.
/// </summary>
/// <remarks>
/// <para>
/// Modelled directly on <see cref="BotNexus.Domain.Security.WebhookSecret"/>, which exists for the
/// same reason on the webhook path: a bare <see cref="string"/> gives the compiler no way to tell a
/// live credential from a display name, so every call site has to remember not to log it. This type
/// moves that from a convention to a property of the type.
/// </para>
/// <para>
/// Two guarantees, both enforced here rather than at each call site:
/// </para>
/// <list type="number">
/// <item><description>
/// <b>It cannot leak by accident.</b> <see cref="ToString"/> returns a redacted marker and
/// <c>PrintMembers</c> is suppressed, so interpolation into a log message, a <c>{Token}</c>
/// structured-logging placeholder, or a record's generated rendering emits the marker rather than
/// the credential. Reaching the raw value requires the explicit, greppable <see cref="Reveal"/>.
/// </description></item>
/// <item><description>
/// <b>A <c>default</c> instance holds nothing.</b> The constructor is private and the only route in
/// is <see cref="TryCreate"/>, so an unset token is representable and distinguishable from a valid
/// one instead of being an empty string that silently authenticates as nobody.
/// </description></item>
/// </list>
/// <para>
/// Unlike <c>WebhookSecret</c> this applies no character-set validation. Homeserver
/// implementations differ in how they mint tokens (Synapse's <c>syt_…</c> is base64url with
/// separators; others differ), so an allow-list here would reject legitimate credentials for no
/// security benefit — the homeserver is the authority on its own token format. Only emptiness is
/// rejected, because an empty token is unambiguously a misconfiguration.
/// </para>
/// </remarks>
public readonly record struct MatrixAccessToken
{
    /// <summary>Rendered in place of the token by <see cref="ToString"/>.</summary>
    public const string RedactedMarker = "MatrixAccessToken(redacted)";

    private readonly string? _value;

    private MatrixAccessToken(string value) => _value = value;

    /// <summary>
    /// True when this instance was produced by <see cref="TryCreate"/>. A
    /// <c>default(MatrixAccessToken)</c> is false and holds no credential.
    /// </summary>
    public bool HasValue => _value is not null;

    /// <summary>
    /// Number of characters in the token, or zero for a <c>default</c> instance. Exposed because
    /// diagnostics often want to state that a credential was present without revealing it.
    /// </summary>
    public int Length => _value?.Length ?? 0;

    /// <summary>
    /// Returns the raw token. Deliberately a named method rather than a property so every unwrap is
    /// greppable, and so it is never picked up implicitly by serialisers, structured logging, or
    /// string interpolation.
    /// </summary>
    /// <exception cref="InvalidOperationException">The instance is <c>default</c> and holds no token.</exception>
    public string Reveal() => _value
        ?? throw new InvalidOperationException(
            "MatrixAccessToken has no value; it was never created through TryCreate.");

    /// <summary>
    /// Attempts to wrap a configured access token.
    /// </summary>
    /// <param name="value">Candidate token from configuration; may be null.</param>
    /// <param name="token">The wrapped token on success; <c>default</c> on failure.</param>
    /// <returns>True when <paramref name="value"/> is non-null and not whitespace.</returns>
    public static bool TryCreate(string? value, out MatrixAccessToken token)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            token = default;
            return false;
        }

        token = new MatrixAccessToken(value);
        return true;
    }

    /// <summary>
    /// Returns a redacted marker, never the token. This is what makes accidental disclosure through
    /// interpolation, structured-log placeholders, or exception messages impossible.
    /// </summary>
    public override string ToString() => RedactedMarker;

    /// <summary>
    /// Suppresses the compiler-generated record member printing, which would otherwise emit the
    /// backing field through <c>ToString</c>-adjacent paths.
    /// </summary>
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("redacted");
        return true;
    }
}
