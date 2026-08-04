using System.Text.Json;
using System.Text.Json.Nodes;

namespace BotNexus.Cli.Services;

/// <summary>
/// The outcome of resolving a gateway HTTP client. Either a usable <see cref="Client"/>
/// or a refusal carrying an operator-facing explanation - never both, and never neither.
/// Modelled as a result rather than an exception because the refusal is an ordinary,
/// expected operator mistake (pointing <c>--url</c> at a remote host without a token),
/// and callers render it as a CLI diagnostic with exit code 1 rather than a stack trace.
/// </summary>
/// <param name="Client">The configured client, or <c>null</c> when refused.</param>
/// <param name="RefusalMessage">Actionable refusal text, or <c>null</c> when resolved.</param>
internal sealed record GatewayClientResolution(HttpClient? Client, string? RefusalMessage)
{
    /// <summary>True when no request may be sent. Callers must not proceed.</summary>
    public bool IsRefused => Client is null;
}

/// <summary>
/// The single place the BotNexus CLI decides how to build a gateway-API <see cref="HttpClient"/>.
///
/// <para>WHY THIS LIVES IN ONE PLACE: before issue #2747 there were at least four independent
/// <c>new HttpClient</c> constructions across the gateway-facing commands
/// (<c>CronCommands</c>, <c>ConversationCommands</c>, <c>DebugGatewayCommand</c>). Each one
/// silently encoded its own answer to "does this request carry a credential, and to whom may
/// we send it?" - and all four answered "no credential, to whoever the operator typed". A
/// security property that must hold for *every* gateway call cannot be enforced from four
/// separate definitions: adding a fifth command reintroduces the defect by omission. A second
/// notion of "how we build the gateway client" is exactly the duplicated-definition defect
/// this type exists to remove, so new gateway commands must call
/// <see cref="Resolve(string, TimeSpan, string?, IGatewayCredentialSource, HttpMessageHandler?)"/>
/// rather than constructing a client themselves.</para>
///
/// <para>THE RULE, stated once: a credential resolved for the local gateway belongs to the
/// local gateway. It is attached when the target is loopback, and it is never attached to a
/// host the operator supplied on the command line - that is the credential-leak direction.
/// Targeting a non-loopback host therefore requires an explicit <c>--token</c>; without one
/// the command refuses rather than sending an unauthenticated request. Ambiguity (an
/// unparseable URL, a blank token) fails closed.</para>
/// </summary>
internal static class GatewayClientFactory
{
    /// <summary>
    /// Header the gateway's API-key auth handler reads. Kept here so the one factory owns
    /// the wire format as well as the policy.
    /// </summary>
    public const string CredentialHeaderName = "X-Api-Key";

    /// <summary>Environment variable holding the local gateway credential.</summary>
    public const string CredentialEnvironmentVariable = "BOTNEXUS_API_KEY";

    /// <summary>The default gateway target every gateway-facing command declares for <c>--url</c>.</summary>
    public const string DefaultUrl = "http://localhost:5005";

    /// <summary>
    /// Resolves a gateway client for <paramref name="baseUrl"/>, applying the credential
    /// policy described on this type. Returns a refusal instead of a client when sending
    /// would be unauthenticated against an operator-supplied host, or when the target
    /// cannot be classified with confidence.
    /// </summary>
    /// <param name="baseUrl">The resolved <c>--url</c> value.</param>
    /// <param name="timeout">Per-request timeout for this command family.</param>
    /// <param name="explicitToken">The operator-supplied <c>--token</c>, if any.</param>
    /// <param name="credentialSource">Source of the ambient local credential.</param>
    /// <param name="handler">Optional transport, for tests. Owned by the returned client.</param>
    public static GatewayClientResolution Resolve(
        string baseUrl,
        TimeSpan timeout,
        string? explicitToken,
        IGatewayCredentialSource credentialSource,
        HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(credentialSource);

        var hasExplicitToken = !string.IsNullOrWhiteSpace(explicitToken);

        if (!TryCreateBaseAddress(baseUrl, out var baseAddress))
        {
            handler?.Dispose();
            return new GatewayClientResolution(
                null,
                $"Cannot target '{baseUrl}': it is not a valid absolute http/https gateway URL. " +
                "Supply a URL such as http://localhost:5005 or https://gateway.example.com.");
        }

        var isLocalDefault = IsLocalDefaultTarget(baseUrl);

        // Clause 3 of #2747: an overridden target with no explicit credential is refused.
        // Falling back to the ambient local credential here would be the leak; sending
        // nothing would be the silent-unauthenticated-request defect. So: refuse.
        if (!isLocalDefault && !hasExplicitToken)
        {
            handler?.Dispose();
            return new GatewayClientResolution(
                null,
                $"Refusing to contact '{baseUrl}' without an explicit credential. " +
                "The credential configured for the local gateway is never sent to a URL supplied " +
                "on the command line. Pass --token <value> to authenticate against this target, " +
                $"or omit --url to use the local gateway at {DefaultUrl}.");
        }

        // An explicit token always wins - it is the operator naming the credential for this
        // specific target. Only when there is none, and only for a loopback target, do we
        // reach for the ambient local credential.
        var credential = hasExplicitToken
            ? explicitToken!.Trim()
            : isLocalDefault ? Normalise(credentialSource.GetGatewayCredential()) : null;

        var client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        client.BaseAddress = baseAddress;
        client.Timeout = timeout;

        if (credential is not null)
            client.DefaultRequestHeaders.Add(CredentialHeaderName, credential);

        return new GatewayClientResolution(client, null);
    }

    /// <summary>
    /// Applies the same credential policy to an <b>already-constructed</b> client, for the
    /// command family that receives its <see cref="HttpClient"/> by injection rather than
    /// building one (<c>cron</c>). Returns <c>null</c> when the client may be used, or the
    /// operator-facing refusal text when it may not.
    ///
    /// <para>This exists so an injected client cannot become a SECOND definition of the
    /// policy. The classification and the credential decision are made by the same code as
    /// <see cref="Resolve"/>; only the client's construction differs.</para>
    /// </summary>
    public static string? ApplyPolicy(
        HttpClient client,
        string baseUrl,
        string? explicitToken,
        IGatewayCredentialSource credentialSource)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(credentialSource);

        var resolution = Resolve(baseUrl, client.Timeout, explicitToken, credentialSource);
        if (resolution.Client is null)
            return resolution.RefusalMessage;

        using var template = resolution.Client;
        client.BaseAddress ??= template.BaseAddress;

        // Re-stamp rather than append: a shared injected client is reused across
        // subcommands, and a stale credential from a previous target must never ride along.
        client.DefaultRequestHeaders.Remove(CredentialHeaderName);
        if (template.DefaultRequestHeaders.TryGetValues(CredentialHeaderName, out var values))
            client.DefaultRequestHeaders.Add(CredentialHeaderName, values);

        return null;
    }


    /// <summary>
    /// True when <paramref name="baseUrl"/> names the loopback gateway this CLI installation
    /// manages, and is therefore the only target the ambient local credential may be sent to.
    /// Anything that cannot be parsed, or is not http/https loopback, is treated as remote:
    /// misclassifying a remote host as local would leak the credential, so this fails closed.
    /// </summary>
    public static bool IsLocalDefaultTarget(string? baseUrl)
    {
        if (!TryCreateBaseAddress(baseUrl, out var uri))
            return false;

        return uri.IsLoopback;
    }

    /// <summary>
    /// Reads the ambient local gateway credential from <c>BOTNEXUS_API_KEY</c>, falling back
    /// to the <c>apiKey</c> field of the resolved <c>config.json</c>. Used for the local
    /// target only - see the policy note on <see cref="GatewayClientFactory"/>.
    /// </summary>
    public static IGatewayCredentialSource DefaultCredentialSource(string? target = null)
        => new EnvironmentAndConfigCredentialSource(target);

    private static bool TryCreateBaseAddress(string? baseUrl, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(baseUrl))
            return false;

        if (!Uri.TryCreate(baseUrl.Trim().TrimEnd('/'), UriKind.Absolute, out var parsed))
            return false;

        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
            return false;

        uri = parsed;
        return true;
    }

    private static string? Normalise(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Production credential source. Kept private so the environment/config lookup order is
    /// stated exactly once, next to the policy that consumes it.
    /// </summary>
    private sealed class EnvironmentAndConfigCredentialSource(string? target) : IGatewayCredentialSource
    {
        public string? GetGatewayCredential()
        {
            var fromEnvironment = Environment.GetEnvironmentVariable(CredentialEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(fromEnvironment))
                return fromEnvironment.Trim();

            try
            {
                var configPath = Path.Combine(CliPaths.ResolveTarget(target), "config.json");
                if (!File.Exists(configPath))
                    return null;

                var root = JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject;
                if (root?["apiKey"] is JsonValue value &&
                    value.TryGetValue<string>(out var apiKey) &&
                    !string.IsNullOrWhiteSpace(apiKey))
                {
                    return apiKey.Trim();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // An unreadable or malformed config yields no credential rather than a crash;
                // the caller then either runs unauthenticated against loopback (fine) or is
                // refused for a remote target (also fine). Fail closed, quietly.
            }

            return null;
        }
    }
}
