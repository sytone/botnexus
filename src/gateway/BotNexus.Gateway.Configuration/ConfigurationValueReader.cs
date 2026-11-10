using System.Text;
using BotNexus.Domain.Security;
using Microsoft.Extensions.Configuration;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Resolves secret templates only when a caller explicitly requests an expanded configuration value.
/// </summary>
public interface IConfigurationValueReader
{
    /// <summary>Reads the configured value unchanged, including any secret-reference templates.</summary>
    string? GetRawValue(IConfiguration configuration, string key);

    /// <summary>
    /// Reads a value and replaces each <c>[[scheme:identifier]]</c> template with its resolved value.
    /// Unavailable or malformed references are replaced with <see cref="InvalidSchemaIdentifier"/>.
    /// </summary>
    Task<string?> GetExpandedValueAsync(
        IConfiguration configuration,
        string key,
        CancellationToken cancellationToken = default);

    /// <summary>Marker returned in place of malformed or unresolved secret references.</summary>
    public const string InvalidSchemaIdentifier = "INVALID_SCHEMA_IDENTIFIER";
}

/// <summary>Resolves one parsed reference for an opted-in configuration read.</summary>
public interface IConfigurationSecretReferenceResolver
{
    /// <summary>
    /// Resolves a parsed reference, or returns <see langword="null"/> when its value is unavailable.
    /// </summary>
    Task<string?> ResolveAsync(SecretRef reference, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class ConfigurationValueReader(IConfigurationSecretReferenceResolver secretResolver)
    : IConfigurationValueReader
{
    private const string ReferenceStart = "[[";
    private const string ReferenceEnd = "]]";

    /// <inheritdoc />
    public string? GetRawValue(IConfiguration configuration, string key)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        return configuration[key];
    }

    /// <inheritdoc />
    public async Task<string?> GetExpandedValueAsync(
        IConfiguration configuration,
        string key,
        CancellationToken cancellationToken = default)
    {
        var value = GetRawValue(configuration, key);
        if (value is null || !value.Contains(ReferenceStart, StringComparison.Ordinal))
            return value;

        var result = new StringBuilder(value.Length);
        var position = 0;
        while (position < value.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var start = value.IndexOf(ReferenceStart, position, StringComparison.Ordinal);
            if (start < 0)
            {
                result.Append(value, position, value.Length - position);
                break;
            }

            result.Append(value, position, start - position);
            var end = value.IndexOf(ReferenceEnd, start + ReferenceStart.Length, StringComparison.Ordinal);
            if (end < 0)
            {
                result.Append(IConfigurationValueReader.InvalidSchemaIdentifier);
                break;
            }

            var referenceText = value.Substring(
                start + ReferenceStart.Length,
                end - start - ReferenceStart.Length);
            if (!SecretRef.TryParse(referenceText, out var reference, out _))
            {
                result.Append(IConfigurationValueReader.InvalidSchemaIdentifier);
            }
            else
            {
                var resolved = await secretResolver.ResolveAsync(reference, cancellationToken).ConfigureAwait(false);
                result.Append(resolved ?? IConfigurationValueReader.InvalidSchemaIdentifier);
            }

            position = end + ReferenceEnd.Length;
        }

        return result.ToString();
    }
}
