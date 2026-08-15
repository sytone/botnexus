using System.Security.Cryptography;
using System.Text;

namespace BotNexus.Domain.Security;

/// <summary>
/// A syntactically valid webhook secret token that can only be created through validation, and
/// which refuses to render its own value when formatted.
/// </summary>
/// <remarks>
/// <para>
/// This exists because a webhook secret is the sole authenticator of inbound webhook traffic, and a
/// bare <see cref="string"/> gives the compiler no way to tell a validated secret from a log line,
/// a display name, or raw user input. Two properties are enforced here rather than by convention at
/// every call site:
/// </para>
/// <list type="number">
/// <item><description>
/// <b>It cannot exist unvalidated.</b> The constructor is private and there is no conversion from
/// <see cref="string"/>, so the only route in is <see cref="TryCreate(string?, out WebhookSecret)"/>
/// or <see cref="Create(string)"/>. A <c>default</c> instance carries no value and is rejected by
/// every comparison.
/// </description></item>
/// <item><description>
/// <b>It cannot leak by accident.</b> <see cref="ToString"/> returns a redacted marker, so string
/// interpolation into a log message, an exception, or a structured-logging sink emits the marker
/// rather than the secret. Reaching the raw value requires the explicit, greppable
/// <see cref="Reveal"/> call.
/// </description></item>
/// </list>
/// <para>
/// Equality is constant-time: record structs would otherwise synthesise an ordinary
/// <see cref="string"/> comparison, which short-circuits on the first differing character and is
/// exactly the timing oracle this type is meant to close.
/// </para>
/// </remarks>
public readonly record struct WebhookSecret
{
    /// <summary>
    /// Maximum token length accepted by the Telegram Bot API for <c>secret_token</c>. The same bound
    /// is applied to every webhook secret so one validation rule serves all channels.
    /// </summary>
    public const int MaxLength = 256;

    /// <summary>Rendered in place of the secret by <see cref="ToString"/>.</summary>
    public const string RedactedMarker = "WebhookSecret(redacted)";

    private readonly string? _value;

    private WebhookSecret(string value) => _value = value;

    /// <summary>
    /// True when this instance was produced by a validating factory. A <c>default(WebhookSecret)</c>
    /// is false and can never match anything.
    /// </summary>
    public bool HasValue => _value is not null;

    /// <summary>
    /// Number of characters in the secret, or zero for a <c>default</c> instance. Exposed because
    /// diagnostics frequently want to state that a secret was present without revealing it.
    /// </summary>
    public int Length => _value?.Length ?? 0;

    /// <summary>
    /// Returns the raw secret. Deliberately a named method rather than a property so every site that
    /// unwraps the secret is greppable, and so it is never picked up implicitly by serialisers,
    /// structured logging, or string interpolation.
    /// </summary>
    /// <exception cref="InvalidOperationException">The instance is <c>default</c> and holds no secret.</exception>
    public string Reveal() => _value
        ?? throw new InvalidOperationException("WebhookSecret has no value; it was never created through a validating factory.");

    /// <summary>
    /// Attempts to create a secret from raw configuration or header input.
    /// </summary>
    /// <param name="value">Candidate token; may be null.</param>
    /// <param name="secret">The validated secret on success; <c>default</c> on failure.</param>
    /// <returns>True when <paramref name="value"/> is non-empty, at most <see cref="MaxLength"/>
    /// characters, and composed only of <c>A-Z a-z 0-9 _ -</c>.</returns>
    public static bool TryCreate(string? value, out WebhookSecret secret)
    {
        secret = default;

        if (string.IsNullOrEmpty(value) || value.Length > MaxLength)
            return false;

        foreach (var c in value)
        {
            if (!IsAllowedChar(c))
                return false;
        }

        secret = new WebhookSecret(value);
        return true;
    }

    /// <summary>
    /// Creates a secret from raw input, throwing when it is not a valid token.
    /// </summary>
    /// <exception cref="ArgumentException">The value is empty, too long, or outside the allowed character set.</exception>
    public static WebhookSecret Create(string value)
        => TryCreate(value, out var secret)
            ? secret
            : throw new ArgumentException(
                $"Value is not a valid webhook secret (allowed: A-Z a-z 0-9 _ -, length 1-{MaxLength}).",
                nameof(value));

    /// <summary>
    /// Generates a cryptographically strong, URL-safe secret. 32 bytes of entropy produce 43
    /// characters, all of which fall inside the allowed set and length bound.
    /// </summary>
    public static WebhookSecret Generate()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        // URL-safe base64 (RFC 4648 §5): '+'/'/' become '-'/'_', '=' padding is stripped.
        var token = Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
        return new WebhookSecret(token);
    }

    /// <summary>
    /// Constant-time equality. Both sides are SHA-256 hashed first so the fixed-length digest
    /// comparison cannot short-circuit on a length difference — the comparison time is independent
    /// of both the length and the content of either operand. A <c>default</c> instance never matches.
    /// </summary>
    public bool Equals(WebhookSecret other)
    {
        if (_value is null || other._value is null)
            return false;

        Span<byte> mine = stackalloc byte[32];
        Span<byte> theirs = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(_value), mine);
        SHA256.HashData(Encoding.UTF8.GetBytes(other._value), theirs);
        return CryptographicOperations.FixedTimeEquals(mine, theirs);
    }

    /// <summary>
    /// Derived from the SHA-256 digest so the hash code is consistent with <see cref="Equals"/> and
    /// does not expose the plaintext through a trivially reversible hash.
    /// </summary>
    public override int GetHashCode()
    {
        if (_value is null)
            return 0;

        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(_value), digest);
        return BitConverter.ToInt32(digest[..4]);
    }

    /// <summary>
    /// Returns a redacted marker, never the secret. This is what makes accidental disclosure through
    /// interpolation, <c>{Secret}</c> structured-log placeholders, or exception messages impossible.
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

    private static bool IsAllowedChar(char c)
        => (c >= 'A' && c <= 'Z')
           || (c >= 'a' && c <= 'z')
           || (c >= '0' && c <= '9')
           || c == '_'
           || c == '-';
}
