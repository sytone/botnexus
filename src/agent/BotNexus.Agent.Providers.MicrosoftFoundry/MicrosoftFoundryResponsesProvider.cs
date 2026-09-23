using System.Collections.Frozen;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.Core.Streaming;
using BotNexus.Agent.Providers.Core.Utilities;
using BotNexus.Agent.Providers.OpenAI;
using BotNexus.Gateway.Abstractions.Security;
using Microsoft.Extensions.Logging;

namespace BotNexus.Agent.Providers.MicrosoftFoundry;

/// <summary>
/// Describes one named Microsoft Foundry Responses endpoint and its instance-owned authentication policy.
/// </summary>
public sealed record MicrosoftFoundryResponsesInstance(
    string ProviderName,
    Uri ApprovedOrigin,
    MicrosoftFoundryAuthentication Authentication);

/// <summary>
/// Dispatches the fixed Microsoft Foundry Responses API contract to immutable named endpoint instances.
/// Register this provider once; models select an instance by exact <see cref="LlmModel.Provider"/>.
/// </summary>
public sealed class MicrosoftFoundryResponsesProvider : IApiProvider, IDisposable
{
    private static readonly ProviderCapabilities ProviderCapabilities = new(
        RecoversLeakedToolCallMarkup: false,
        SystemPromptPlacement: SystemPromptPlacement.FirstMessage);
    private readonly FrozenDictionary<string, Instance> _instances;
    private readonly ILogger<MicrosoftFoundryResponsesProvider> _logger;
    private readonly ISecretRedactor? _secretRedactor;

    /// <summary>
    /// Creates one registry provider that owns all configured named Foundry instances.
    /// </summary>
    public MicrosoftFoundryResponsesProvider(
        IEnumerable<MicrosoftFoundryResponsesInstance> instances,
        ILogger<MicrosoftFoundryResponsesProvider> logger,
        ISecretRedactor? secretRedactor = null)
        : this(instances, static _ => CreateSecureHandler(), logger, secretRedactor)
    {
    }

    internal MicrosoftFoundryResponsesProvider(
        IEnumerable<MicrosoftFoundryResponsesInstance> instances,
        Func<string, HttpMessageHandler> createHandler,
        ILogger<MicrosoftFoundryResponsesProvider> logger,
        ISecretRedactor? secretRedactor = null)
    {
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentNullException.ThrowIfNull(createHandler);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _secretRedactor = secretRedactor;

        var mapped = new Dictionary<string, Instance>(StringComparer.Ordinal);
        foreach (var configuration in instances)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentException.ThrowIfNullOrWhiteSpace(configuration.ProviderName);
            if (!mapped.TryAdd(configuration.ProviderName, new Instance(configuration, createHandler(configuration.ProviderName))))
                throw new ArgumentException($"Duplicate Microsoft Foundry provider name '{configuration.ProviderName}'.", nameof(instances));
        }

        if (mapped.Count == 0)
            throw new ArgumentException("At least one Microsoft Foundry instance is required.", nameof(instances));

        _instances = mapped.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public string Api => "microsoft-foundry-responses";

    /// <inheritdoc />
    public ProviderCapabilities Capabilities => ProviderCapabilities;

    /// <inheritdoc />
    public LlmStream Stream(LlmModel model, Context context, StreamOptions? options = null)
    {
        var instance = ResolveInstance(model);
        return ResponsesStreamEngine.StreamAsync(
            BuildProfile(instance), instance.HttpClient, _logger, model, context, options);
    }

    /// <inheritdoc />
    public LlmStream StreamSimple(LlmModel model, Context context, SimpleStreamOptions? options = null)
    {
        var streamOptions = SimpleOptionsHelper.BuildBaseOptions(model, options, string.Empty);
        return Stream(model, context, streamOptions);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var instance in _instances.Values)
            instance.Dispose();
    }

    internal static HttpClientHandler CreateSecureHandler() => new()
    {
        AllowAutoRedirect = false
    };

    private ResponsesTransportProfile BuildProfile(Instance instance) => OpenAIResponsesTransport.CreateProfile(
        _logger,
        api: Api,
        activityName: "provider.microsoft-foundry-responses.stream",
        errorProviderName: "Microsoft Foundry",
        secretRedactor: _secretRedactor,
        buildRequestUri: instance.BuildRequestUri,
        authenticateRequest: instance.AuthenticationResolver.ApplyAsync,
        resolveApiKey: false);

    private Instance ResolveInstance(LlmModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (_instances.TryGetValue(model.Provider, out var instance))
            return instance;

        throw new InvalidOperationException($"No Microsoft Foundry instance is configured for model provider '{model.Provider}'.");
    }

    private static Uri NormalizeApprovedOrigin(Uri origin)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if (!origin.IsAbsoluteUri || origin.Scheme != Uri.UriSchemeHttps || origin.IsDefaultPort is false)
            throw new InvalidOperationException("Microsoft Foundry origin must use HTTPS on the default port.");
        if (origin.UserInfo.Length > 0 || origin.Query.Length > 0 || origin.Fragment.Length > 0)
            throw new InvalidOperationException("Microsoft Foundry origin cannot contain credentials, query, or fragment components.");

        return new UriBuilder(Uri.UriSchemeHttps, origin.IdnHost).Uri;
    }

    private sealed class Instance : IDisposable
    {
        private readonly string _providerName;
        private readonly Uri _approvedOrigin;

        public Instance(MicrosoftFoundryResponsesInstance configuration, HttpMessageHandler handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            _providerName = configuration.ProviderName;
            _approvedOrigin = NormalizeApprovedOrigin(configuration.ApprovedOrigin);
            AuthenticationResolver = new MicrosoftFoundryAuthenticationResolver(configuration.Authentication);
            HttpClient = new HttpClient(handler, disposeHandler: true);
        }

        public HttpClient HttpClient { get; }

        public MicrosoftFoundryAuthenticationResolver AuthenticationResolver { get; }

        public Uri BuildRequestUri(LlmModel model)
        {
            if (!Uri.TryCreate(model.BaseUrl, UriKind.Absolute, out var modelOrigin))
                throw new InvalidOperationException("Microsoft Foundry base URL must be an absolute HTTPS origin.");

            var normalizedModelOrigin = NormalizeApprovedOrigin(modelOrigin);
            if (normalizedModelOrigin != _approvedOrigin)
                throw new InvalidOperationException(
                    $"Microsoft Foundry origin '{normalizedModelOrigin}' is not the approved origin '{_approvedOrigin}' for provider '{_providerName}'.");

            return new Uri(_approvedOrigin, "/openai/v1/responses");
        }

        public void Dispose() => HttpClient.Dispose();
    }
}
