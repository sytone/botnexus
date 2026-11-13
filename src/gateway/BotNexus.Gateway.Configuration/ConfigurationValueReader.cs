using System.Text;
using Microsoft.Extensions.Configuration;

namespace BotNexus.Gateway.Configuration;

/// <summary>Reads configuration values either unchanged or with reference tokens expanded.</summary>
public interface IConfigurationValueReader
{
    /// <summary>Reads the configured value unchanged, including any reference tokens.</summary>
    string? GetRawValue(IConfiguration configuration, string key);

    /// <summary>Reads a configured value and expands its <c>[[scheme:identifier]]</c> tokens.</summary>
    Task<string?> GetExpandedValueAsync(
        IConfiguration configuration,
        string key,
        CancellationToken cancellationToken = default);

    /// <summary>Expands reference tokens in a value that has already been read or bound.</summary>
    Task<string?> ExpandValueAsync(string? value, CancellationToken cancellationToken = default);

    /// <summary>Marker substituted for malformed or unavailable reference tokens.</summary>
    public const string InvalidSchemaIdentifier = "INVALID_SCHEMA_IDENTIFIER";
}

/// <summary>Resolves one configuration reference scheme, such as <c>file</c> or <c>env</c>.</summary>
public interface IConfigurationReferenceResolver
{
    /// <summary>The scheme this resolver handles, without the trailing colon.</summary>
    string Scheme { get; }

    /// <summary>Returns the referenced value, or <see langword="null"/> when it is unavailable.</summary>
    Task<string?> ResolveAsync(string identifier, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class ConfigurationValueReader : IConfigurationValueReader
{
    private const string ReferenceStart = "[[";
    private const string ReferenceEnd = "]]";
    private readonly IReadOnlyDictionary<string, IConfigurationReferenceResolver> _resolvers;

    /// <summary>Creates a reader with the registered scheme resolvers.</summary>
    public ConfigurationValueReader(IEnumerable<IConfigurationReferenceResolver> resolvers)
    {
        ArgumentNullException.ThrowIfNull(resolvers);
        _resolvers = resolvers.ToDictionary(resolver => resolver.Scheme, StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public string? GetRawValue(IConfiguration configuration, string key)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return configuration[key];
    }

    /// <inheritdoc />
    public Task<string?> GetExpandedValueAsync(
        IConfiguration configuration,
        string key,
        CancellationToken cancellationToken = default)
    {
        var value = GetRawValue(configuration, key);
        return ExpandValueAsync(value, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<string?> ExpandValueAsync(string? value, CancellationToken cancellationToken = default)
    {
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

            var token = value.Substring(start + ReferenceStart.Length, end - start - ReferenceStart.Length);
            var separator = token.IndexOf(':');
            var scheme = separator > 0 ? token[..separator] : string.Empty;
            var identifier = separator > 0 ? token[(separator + 1)..] : string.Empty;
            if (!IsValidScheme(scheme) || identifier.Length == 0
                || !_resolvers.TryGetValue(scheme, out var resolver))
            {
                result.Append(IConfigurationValueReader.InvalidSchemaIdentifier);
            }
            else
            {
                string? resolved;
                try
                {
                    resolved = await resolver.ResolveAsync(identifier, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is ArgumentException or IOException
                    or UnauthorizedAccessException or NotSupportedException or InvalidOperationException
                    or System.Security.SecurityException)
                {
                    resolved = null;
                }

                result.Append(resolved ?? IConfigurationValueReader.InvalidSchemaIdentifier);
            }

            position = end + ReferenceEnd.Length;
        }

        return result.ToString();
    }

    private static bool IsValidScheme(string scheme)
        => scheme.Length > 0 && scheme.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}
