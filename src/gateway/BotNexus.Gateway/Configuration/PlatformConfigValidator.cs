using System.Collections;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Pure validation engine for <see cref="PlatformConfig"/>. Extracted from
/// <see cref="PlatformConfigLoader"/> (#1764) so the "does this materialised config satisfy the
/// platform rules?" question is answerable in isolation from load/materialize/migrate and their
/// filesystem concerns. Every method here is a pure function of an already-materialised
/// <see cref="PlatformConfig"/> graph: <c>(PlatformConfig) -&gt; IReadOnlyList&lt;string&gt;</c> with
/// zero file I/O, which is what makes the rules trivially unit-testable without config-file fixtures.
/// The loader retains one-line forwarding shims for the historical public entry points so existing
/// callers keep compiling.
/// </summary>
public static class PlatformConfigValidator
{
    private const int SupportedConfigVersion = 1;

    /// <summary>Validates non-fatal configuration concerns and returns warnings.</summary>
    public static IReadOnlyList<string> ValidateWarnings(PlatformConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        List<string> warnings = [];

        if (config.PlatformVersion > SupportedConfigVersion)
        {
            warnings.Add(
                $"version '{config.PlatformVersion}' is newer than supported version '{SupportedConfigVersion}'. " +
                "The gateway will continue with best-effort compatibility.");
        }

        ValidateLocationWarnings(config.Gateway?.Locations, warnings);

        return warnings;
    }

    /// <summary>
    /// Validates the configuration and returns any errors.
    /// </summary>
    /// <remarks>
    /// Retained as the legacy/baseline validation surface and as the imperative cross-field and
    /// graph escape hatch for the DataAnnotations pipeline (#1613). The body lives in
    /// <see cref="CollectCrossFieldErrors"/> so it can be invoked both directly (existing callers
    /// and the parity baseline) and from <see cref="PlatformConfig.Validate(ValidationContext)"/>
    /// during a <see cref="Validator.TryValidateObject"/> pass without duplicating the rules.
    /// </remarks>
    public static IReadOnlyList<string> Validate(PlatformConfig config)
        => CollectCrossFieldErrors(config);

    /// <summary>
    /// Runs server-side configuration validation through the DataAnnotations pipeline (#1613,
    /// config parity PBI 5/6 of #1579): <see cref="Validator.TryValidateObject"/> over the annotated
    /// <see cref="PlatformConfig"/> with <c>validateAllProperties: true</c>. This fires the per-field
    /// validation attributes (for example <see cref="RangeAttribute"/>) declared on the model AND the
    /// cross-field escape hatch in <see cref="PlatformConfig.Validate(ValidationContext)"/>, which
    /// delegates back to <see cref="CollectCrossFieldErrors"/>. Messages are de-duplicated so a field
    /// covered by both an attribute and an imperative rule is reported once.
    /// </summary>
    /// <param name="config">The platform configuration to validate.</param>
    /// <returns>The distinct validation error messages, or an empty list when the config is valid.</returns>
    public static IReadOnlyList<string> ValidateAnnotated(PlatformConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        // #2061: Validator.TryValidateObject only fires attributes declared on the ROOT object
        // and never recurses into nested POCOs, dictionary values, or list elements. The shared
        // recursive walker (ValidateGraphAnnotations) descends the whole materialised config graph
        // and executes the per-field DataAnnotations at every level, returning path-prefixed
        // messages (for example "gateway.autoUpdate.checkIntervalMinutes: ..."). The root
        // IValidatableObject cross-field escape hatch is invoked once here (member-less, so its
        // messages stay verbatim - the platform's exact legacy text) and merged in.
        var errors = new List<string>();

        // Root cross-field rules (IValidatableObject) - verbatim messages, no path prefix.
        var rootResults = new List<ValidationResult>();
        var rootContext = new ValidationContext(config);
        _ = Validator.TryValidateObject(
            config, rootContext, rootResults, validateAllProperties: false);
        errors.AddRange(rootResults
            .Select(result => result.ErrorMessage ?? string.Empty)
            .Where(message => !string.IsNullOrWhiteSpace(message)));

        // Recursive per-field DataAnnotations across the complete object graph.
        errors.AddRange(ValidateGraphAnnotations(config));

        return errors
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Recursively walks the complete config object graph rooted at <paramref name="root"/> and
    /// executes the per-field <see cref="ValidationAttribute"/> DataAnnotations declared at every
    /// level - nested POCOs, dictionary values, and list/array elements - which the framework's
    /// root-only <see cref="Validator.TryValidateObject"/> pass never reaches (#2061). Each
    /// violation is returned as a single message prefixed with the actionable JSON/member path to
    /// the offending value (for example <c>gateway.autoUpdate.checkIntervalMinutes: ...</c>,
    /// <c>providers.copilot.contextWindow: ...</c>, <c>cors.allowedOrigins[2]: ...</c>) so a caller
    /// can locate the field in <c>config.json</c> without guessing.
    /// </summary>
    /// <remarks>
    /// This is the shared graph walker the platform standardises on: cross-field, conditional, and
    /// dictionary-key-spanning rules that cannot be expressed as a per-field attribute stay in the
    /// imperative <see cref="CollectCrossFieldErrors"/> escape hatch; everything expressible as a
    /// scalar attribute is enforced here at every graph depth. Reference cycles are guarded with an
    /// identity-based visited set so a self-referential graph cannot stack-overflow, and only types
    /// in the BotNexus config namespaces (plus their collection element types) are descended into so
    /// the walk never wanders into framework primitives.
    /// </remarks>
    /// <param name="root">The object graph root to validate. Typically a <see cref="PlatformConfig"/>.</param>
    /// <returns>One path-prefixed message per nested attribute violation; empty when the graph is clean.</returns>
    public static IReadOnlyList<string> ValidateGraphAnnotations(object? root)
    {
        var errors = new List<string>();
        if (root is null)
            return errors;

        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        WalkGraph(root, string.Empty, visited, errors);

        return errors
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Depth-first descent used by <see cref="ValidateGraphAnnotations"/>. Validates the object's
    /// own annotated properties at <paramref name="path"/>, then recurses into each nested
    /// config-typed property, dictionary value, and enumerable element, extending the path.
    /// </summary>
    private static void WalkGraph(object node, string path, HashSet<object> visited, List<string> errors)
    {
        if (node is null || !visited.Add(node))
            return;

        var type = node.GetType();

        // Validate this node's own DataAnnotations (per-field attributes only; the root
        // cross-field IValidatableObject rules are handled once by the caller so they are not
        // re-run here per node). validateAllProperties: true fires all per-property attributes.
        var results = new List<ValidationResult>();
        var context = new ValidationContext(node);
        _ = Validator.TryValidateObject(node, context, results, validateAllProperties: true);
        foreach (var result in results)
        {
            var member = result.MemberNames.FirstOrDefault();
            var memberPath = string.IsNullOrEmpty(member)
                ? path
                : CombinePath(path, ToJsonName(type, member));
            errors.Add(FormatError(memberPath, result.ErrorMessage));
        }

        // Recurse into nested config objects, dictionaries, and collections.
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0 || !property.CanRead)
                continue;

            object? value;
            try
            {
                value = property.GetValue(node);
            }
            catch (TargetInvocationException)
            {
                continue;
            }

            if (value is null)
                continue;

            var jsonName = ToJsonName(type, property.Name);
            var childPath = CombinePath(path, jsonName);
            WalkValue(value, childPath, visited, errors);
        }
    }

    /// <summary>
    /// Recurses into a single property value: descends config-typed objects, iterates dictionary
    /// values (keyed by their string key) and enumerable elements (indexed), and ignores scalars.
    /// </summary>
    private static void WalkValue(object value, string path, HashSet<object> visited, List<string> errors)
    {
        if (value is string || value.GetType().IsPrimitive || value is decimal || value is DateTime
            || value is DateTimeOffset || value is TimeSpan || value is Guid || value is Uri
            || value.GetType().IsEnum)
        {
            return;
        }

        if (value is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                if (entry.Value is null)
                    continue;

                var keyText = entry.Key?.ToString() ?? string.Empty;
                WalkValue(entry.Value, CombinePath(path, keyText), visited, errors);
            }

            return;
        }

        if (value is IEnumerable enumerable)
        {
            var index = 0;
            foreach (var item in enumerable)
            {
                if (item is not null && IsConfigNode(item.GetType()))
                    WalkGraph(item, $"{path}[{index}]", visited, errors);

                index++;
            }

            return;
        }

        if (IsConfigNode(value.GetType()))
            WalkGraph(value, path, visited, errors);
    }

    /// <summary>
    /// Returns <see langword="true"/> for a type the walker should descend into: a class defined
    /// in a BotNexus config/model namespace. This keeps the walk from wandering into framework
    /// primitives, <see cref="System.Text.Json"/> element trees, and other non-config types while
    /// still covering every nested config POCO regardless of which model file declares it.
    /// </summary>
    private static bool IsConfigNode(Type type)
    {
        if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal))
            return false;

        // JsonElement/JsonDocument and other System.Text.Json opaque payloads are never descended.
        if (type.Namespace is { } ns && ns.StartsWith("System", StringComparison.Ordinal))
            return false;

        return type.IsClass;
    }

    /// <summary>
    /// Maps a CLR property name to its serialized JSON name for path reporting: honours an explicit
    /// <see cref="JsonPropertyNameAttribute"/> when present, otherwise camelCases the CLR name so
    /// the reported path matches what the operator sees in <c>config.json</c>.
    /// </summary>
    private static string ToJsonName(Type declaringType, string clrName)
    {
        var property = declaringType.GetProperty(clrName, BindingFlags.Public | BindingFlags.Instance);
        var jsonAttr = property?.GetCustomAttribute<JsonPropertyNameAttribute>();
        if (jsonAttr is not null && !string.IsNullOrEmpty(jsonAttr.Name))
            return jsonAttr.Name;

        return ToCamelCase(clrName);
    }

    private static string ToCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name) || char.IsLower(name[0]))
            return name;

        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    private static string CombinePath(string prefix, string segment)
        => string.IsNullOrEmpty(prefix) ? segment : $"{prefix}.{segment}";

    private static string FormatError(string path, string? message)
    {
        var text = string.IsNullOrWhiteSpace(message) ? "is invalid." : message.Trim();
        return string.IsNullOrEmpty(path) ? text : $"{path}: {text}";
    }

    /// <summary>
    /// The imperative cross-field, dictionary-graph, and conditional validation rules for
    /// <see cref="PlatformConfig"/>. These are the rules that cannot be expressed as per-field
    /// DataAnnotations attributes (they span fields, iterate user-keyed maps, or apply
    /// "if X then Y" logic) and therefore remain the documented escape hatch. Produces the exact
    /// message text the platform has always emitted, so both the legacy <see cref="Validate"/>
    /// surface and the new <see cref="ValidateAnnotated"/> pipeline reject and accept identically.
    /// </summary>
    /// <param name="config">The platform configuration to validate.</param>
    /// <returns>One message per rule violation.</returns>
    public static IReadOnlyList<string> CollectCrossFieldErrors(PlatformConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        List<string> errors = [];

        var listenUrl = config.Gateway?.ListenUrl;
        if (!string.IsNullOrWhiteSpace(listenUrl))
        {
            // ASP.NET/Kestrel binding wildcards (http://+:5000, http://*:5000) are valid
            // listen URLs for app.Urls.Add, but Uri.TryCreate rejects '+' and '*' as hosts.
            // Accept them explicitly (validating only the scheme) so the canonical container
            // config 'http://+:5000' does not crash startup with a validation error.
            if (TryParseBindingWildcard(listenUrl, out var wildcardScheme))
            {
                if (!(wildcardScheme == Uri.UriSchemeHttp || wildcardScheme == Uri.UriSchemeHttps))
                {
                    errors.Add("gateway.listenUrl must use http or https.");
                }
            }
            else if (!Uri.TryCreate(listenUrl, UriKind.Absolute, out var listenUri))
            {
                errors.Add("gateway.listenUrl must be a valid absolute URL (example: http://localhost:5005).");
            }
            else if (!(listenUri.Scheme == Uri.UriSchemeHttp || listenUri.Scheme == Uri.UriSchemeHttps))
            {
                errors.Add("gateway.listenUrl must use http or https.");
            }
        }

        ValidatePath(config.Gateway?.AgentsDirectory, "gateway.agentsDirectory", errors);
        ValidatePath(config.Gateway?.SessionsDirectory, "gateway.sessionsDirectory", errors);
        ValidateLocations(config.Gateway?.Locations, errors);
        ValidateSessionStore(config.Gateway?.SessionStore, errors);
        ValidateCors(config.Gateway?.Cors, errors);
        ValidateCrossWorld(config.Gateway?.CrossWorld, errors);

        var logLevel = config.Gateway?.LogLevel;
        if (!string.IsNullOrWhiteSpace(logLevel) &&
            !Enum.TryParse<LogLevel>(logLevel, ignoreCase: true, out _))
        {
            errors.Add("gateway.logLevel must be one of: Trace, Debug, Information, Warning, Error, Critical.");
        }

        ValidateProviders(config.Providers, errors);
        ValidateChannels(config.Channels, errors);
        ValidateAgents(config.Agents, errors);
        ValidateAgentDefaults(config.AgentDefaults, errors);
        ValidateApiKeys(config.Gateway?.ApiKeys, errors);
        ValidatePromptTemplates(config.PromptTemplates, errors);
        ValidateCron(config.Cron, errors);

        return errors;
    }

    /// <summary>
    /// Recognizes ASP.NET/Kestrel binding-wildcard listen URLs (<c>http://+:5000</c>,
    /// <c>http://*:5000</c>, with or without a port). These are valid for <c>app.Urls.Add</c>
    /// but are rejected by <see cref="Uri.TryCreate(string, UriKind, out Uri)"/> because
    /// <c>+</c> and <c>*</c> are not valid URI hosts. Returns <see langword="true"/> and the
    /// parsed scheme when the value is such a wildcard so the caller can validate the scheme.
    /// </summary>
    private static bool TryParseBindingWildcard(string listenUrl, out string scheme)
    {
        scheme = string.Empty;

        var schemeSeparatorIndex = listenUrl.IndexOf("://", StringComparison.Ordinal);
        if (schemeSeparatorIndex <= 0)
            return false;

        var candidateScheme = listenUrl[..schemeSeparatorIndex];
        var remainder = listenUrl[(schemeSeparatorIndex + 3)..];
        if (remainder.Length == 0)
            return false;

        // Host is everything up to an optional ':<port>'. The wildcard hosts are exactly
        // '+' or '*'. Any path/query after the authority means it is not a bare wildcard.
        var colonIndex = remainder.IndexOf(':');
        var host = colonIndex >= 0 ? remainder[..colonIndex] : remainder;
        if (host != "+" && host != "*")
            return false;

        if (colonIndex >= 0)
        {
            var port = remainder[(colonIndex + 1)..];
            if (port.Length == 0 || !port.All(char.IsAsciiDigit))
                return false;
        }

        scheme = candidateScheme.ToLowerInvariant();
        return true;
    }

    private static void ValidatePath(string? path, string fieldName, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            errors.Add($"{fieldName} contains invalid path characters.");
            return;
        }

        try
        {
            _ = Path.GetFullPath(path);
        }
        catch (Exception)
        {
            errors.Add($"{fieldName} must be a valid path.");
        }
    }

    private static void ValidateProviders(Dictionary<string, ProviderConfig>? providers, List<string> errors)
    {
        if (providers is null)
            return;

        foreach (var (providerKey, providerConfig) in providers)
        {
            if (string.IsNullOrWhiteSpace(providerKey))
            {
                errors.Add("providers contains an empty provider key. Use a provider ID (example: 'copilot').");
                continue;
            }

            if (providerConfig is null)
            {
                errors.Add($"providers.{providerKey} configuration is required.");
                continue;
            }

            if (!providerConfig.Enabled)
                continue;

            // The integration-mock provider repurposes BaseUrl as a filesystem path to
            // a JSON catalog (per IntegrationMockProvider). Skip the http(s) URL check
            // in that case - both the CLI `provider add --base-url` help text and the
            // ProviderConfig.Api docs already describe this carve-out.
            var isIntegrationMock = string.Equals(
                providerConfig.Api, "integration-mock", StringComparison.OrdinalIgnoreCase);

            if (!isIntegrationMock &&
                !string.IsNullOrWhiteSpace(providerConfig.BaseUrl) &&
                (!Uri.TryCreate(providerConfig.BaseUrl, UriKind.Absolute, out var providerUri) ||
                 (providerUri.Scheme != Uri.UriSchemeHttp && providerUri.Scheme != Uri.UriSchemeHttps)))
            {
                errors.Add($"providers.{providerKey}.baseUrl must be a valid http or https absolute URL.");
            }
        }
    }

    private static void ValidateChannels(Dictionary<string, ChannelConfig>? channels, List<string> errors)
    {
        if (channels is null)
            return;

        foreach (var (channelKey, _) in channels)
        {
            if (string.IsNullOrWhiteSpace(channelKey))
                errors.Add("channels contains an empty channel key. Use a channel ID (example: 'web').");
        }
    }

    private static void ValidateCrossWorld(CrossWorldFederationConfig? crossWorld, List<string> errors)
    {
        if (crossWorld is null)
            return;

        if (crossWorld.Peers is not null)
        {
            foreach (var (peerKey, peerConfig) in crossWorld.Peers)
            {
                if (string.IsNullOrWhiteSpace(peerKey))
                {
                    errors.Add("gateway.crossWorld.peers contains an empty peer key.");
                    continue;
                }

                if (!peerConfig.Enabled)
                    continue;

                var worldId = string.IsNullOrWhiteSpace(peerConfig.WorldId) ? peerKey : peerConfig.WorldId;
                if (string.IsNullOrWhiteSpace(worldId))
                    errors.Add($"gateway.crossWorld.peers.{peerKey}.worldId is required.");

                if (string.IsNullOrWhiteSpace(peerConfig.Endpoint) ||
                    !Uri.TryCreate(peerConfig.Endpoint, UriKind.Absolute, out var endpointUri) ||
                    (endpointUri.Scheme != Uri.UriSchemeHttp && endpointUri.Scheme != Uri.UriSchemeHttps))
                {
                    errors.Add($"gateway.crossWorld.peers.{peerKey}.endpoint must be a valid http or https absolute URL.");
                }
            }
        }

        if (crossWorld.Agents is not null)
        {
            foreach (var (agentKey, agentConfig) in crossWorld.Agents)
            {
                if (string.IsNullOrWhiteSpace(agentKey))
                    errors.Add("gateway.crossWorld.agents contains an empty key.");

                if (string.IsNullOrWhiteSpace(agentConfig.WorldId))
                    errors.Add($"gateway.crossWorld.agents.{agentKey}.worldId is required.");

                if (string.IsNullOrWhiteSpace(agentConfig.AgentId))
                    errors.Add($"gateway.crossWorld.agents.{agentKey}.agentId is required.");
            }
        }

        if (crossWorld.Inbound is null || !crossWorld.Inbound.Enabled)
            return;

        if (crossWorld.Inbound.AllowedWorlds is null || crossWorld.Inbound.AllowedWorlds.Count == 0)
        {
            errors.Add("gateway.crossWorld.inbound.allowedWorlds must contain at least one world when inbound is enabled.");
            return;
        }

        if (crossWorld.Inbound.ApiKeys is null)
        {
            errors.Add("gateway.crossWorld.inbound.apiKeys is required when inbound is enabled.");
            return;
        }

        foreach (var worldId in crossWorld.Inbound.AllowedWorlds.Where(world => !string.IsNullOrWhiteSpace(world)))
        {
            if (!crossWorld.Inbound.ApiKeys.TryGetValue(worldId, out var apiKey) || string.IsNullOrWhiteSpace(apiKey))
                errors.Add($"gateway.crossWorld.inbound.apiKeys.{worldId} is required for allowed inbound world '{worldId}'.");
        }
    }

    private static void ValidateAgents(Dictionary<string, AgentDefinitionConfig>? agents, List<string> errors)
    {
        if (agents is null)
            return;

        foreach (var (agentId, agentConfig) in agents)
        {
            if (string.IsNullOrWhiteSpace(agentId))
            {
                errors.Add("agents contains an empty agent ID. Use a stable ID (example: 'assistant').");
                continue;
            }

            // Reserved key - handled separately
            if (string.Equals(agentId, "defaults", StringComparison.OrdinalIgnoreCase))
                continue;

            // #2136: the six built-in worker archetype ids are reserved for
            // spawn_subagent(archetype:...) and may not be defined/overridden as named agents.
            if (Agents.BuiltInArchetypes.IsReserved(agentId))
            {
                errors.Add(
                    $"agents.{agentId} uses a reserved sub-agent archetype id and cannot be defined as a named agent. " +
                    "Use spawn_subagent(archetype: ...) to delegate work to it.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(agentConfig.Provider))
                errors.Add($"agents.{agentId}.provider is required (example: 'copilot').");

            if (string.IsNullOrWhiteSpace(agentConfig.Model))
                errors.Add($"agents.{agentId}.model is required (example: 'gpt-4.1').");

            if (!string.IsNullOrWhiteSpace(agentConfig.Memory?.PromptInjection) &&
                !IsValidMemoryPromptInjection(agentConfig.Memory.PromptInjection))
            {
                errors.Add($"agents.{agentId}.memory.promptInjection must be one of: full, summary, none.");
            }
        }
    }

    private static void ValidateAgentDefaults(AgentDefaultsConfig? defaults, List<string> errors)
    {
        if (defaults is null)
            return;

        const string prefix = "agents.defaults";

        // toolIds
        if (defaults.ToolIds is not null)
        {
            for (var i = 0; i < defaults.ToolIds.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(defaults.ToolIds[i]))
                    errors.Add($"{prefix}.toolIds[{i}] must be a non-empty string.");
            }
        }

        // memory
        if (defaults.Memory is not null)
        {
            // indexing must be non-empty if explicitly set to something other than default
            if (defaults.Memory.Indexing is not null && string.IsNullOrWhiteSpace(defaults.Memory.Indexing))
                errors.Add($"{prefix}.memory.indexing must be a non-empty string if specified.");

            if (!string.IsNullOrWhiteSpace(defaults.Memory.PromptInjection) &&
                !IsValidMemoryPromptInjection(defaults.Memory.PromptInjection))
            {
                errors.Add($"{prefix}.memory.promptInjection must be one of: full, summary, none.");
            }
        }

        // heartbeat
        if (defaults.Heartbeat is not null)
        {
            if (defaults.Heartbeat.IntervalMinutes <= 0)
                errors.Add($"{prefix}.heartbeat.intervalMinutes must be greater than zero.");
        }

        // fileAccess
        if (defaults.FileAccess is not null)
        {
            ValidateFileAccessPaths(defaults.FileAccess.AllowedReadPaths, $"{prefix}.fileAccess.allowedReadPaths", errors);
            ValidateFileAccessPaths(defaults.FileAccess.AllowedWritePaths, $"{prefix}.fileAccess.allowedWritePaths", errors);
            ValidateFileAccessPaths(defaults.FileAccess.DeniedPaths, $"{prefix}.fileAccess.deniedPaths", errors);
        }
    }

    private static void ValidateFileAccessPaths(List<string>? paths, string fieldPath, List<string> errors)
    {
        if (paths is null)
            return;

        for (var i = 0; i < paths.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(paths[i]))
                errors.Add($"{fieldPath}[{i}] must be a non-empty string.");
        }
    }

    private static bool IsValidMemoryPromptInjection(string value) =>
        value.Equals("full", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("summary", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("none", StringComparison.OrdinalIgnoreCase);

    private static void ValidateApiKeys(Dictionary<string, ApiKeyConfig>? apiKeys, List<string> errors)
    {
        if (apiKeys is null)
            return;

        foreach (var (keyId, keyConfig) in apiKeys)
        {
            if (string.IsNullOrWhiteSpace(keyId))
            {
                errors.Add("gateway.apiKeys contains an empty key ID. Use a stable key name (example: 'tenant-a').");
                continue;
            }

            var keyPath = $"gateway.apiKeys.{keyId}";

            if (string.IsNullOrWhiteSpace(keyConfig.ApiKey))
                errors.Add($"{keyPath}.apiKey is required.");

            if (string.IsNullOrWhiteSpace(keyConfig.TenantId))
                errors.Add($"{keyPath}.tenantId is required.");

            if (keyConfig.Permissions is null || keyConfig.Permissions.Count == 0)
                errors.Add($"{keyPath}.permissions must contain at least one permission (example: ['chat:send']).");
        }
    }

    private static void ValidateSessionStore(SessionStoreConfig? sessionStore, List<string> errors)
    {
        if (sessionStore is null)
            return;

        var configuredType = sessionStore.Type?.Trim();
        if (string.IsNullOrWhiteSpace(configuredType))
            return;

        if (configuredType.Equals("InMemory", StringComparison.OrdinalIgnoreCase))
            return;

        if (configuredType.Equals("File", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(sessionStore.FilePath))
            {
                errors.Add("gateway.sessionStore.filePath is required when gateway.sessionStore.type is 'File'.");
                return;
            }

            ValidatePath(sessionStore.FilePath, "gateway.sessionStore.filePath", errors);
            return;
        }

        if (configuredType.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            // connectionString is optional - defaults to sessions.sqlite in config directory
            return;
        }

        errors.Add("gateway.sessionStore.type must be either 'InMemory', 'File', or 'Sqlite'.");
    }

    private static void ValidateLocations(Dictionary<string, LocationConfig>? locations, List<string> errors)
    {
        if (locations is null)
            return;

        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (locationName, locationConfig) in locations)
        {
            if (string.IsNullOrWhiteSpace(locationName))
            {
                errors.Add("gateway.locations contains an empty location key.");
                continue;
            }

            var normalizedName = locationName.Trim();
            if (!seenNames.Add(normalizedName))
            {
                errors.Add($"gateway.locations contains duplicate location name '{normalizedName}' (case-insensitive).");
                continue;
            }

            if (locationConfig is null)
            {
                errors.Add($"gateway.locations.{normalizedName} configuration is required.");
                continue;
            }

            var type = string.IsNullOrWhiteSpace(locationConfig.Type)
                ? "filesystem"
                : locationConfig.Type.Trim();

            var fieldPath = $"gateway.locations.{normalizedName}";

            if (!TryValidateLocationType(type))
                errors.Add($"{fieldPath}.type must be one of: filesystem, api, mcp-server, database, remote-node.");

            if (type.Equals("filesystem", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(locationConfig.Path))
                {
                    errors.Add($"{fieldPath}.path is required for filesystem locations.");
                    continue;
                }

                ValidatePath(locationConfig.Path, $"{fieldPath}.path", errors);
            }
            else if (type.Equals("api", StringComparison.OrdinalIgnoreCase)
                     || type.Equals("mcp-server", StringComparison.OrdinalIgnoreCase)
                     || type.Equals("remote-node", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(locationConfig.Endpoint))
                {
                    errors.Add($"{fieldPath}.endpoint is required for {type} locations.");
                    continue;
                }

                if (!Uri.TryCreate(locationConfig.Endpoint, UriKind.Absolute, out var endpointUri)
                    || (endpointUri.Scheme != Uri.UriSchemeHttp && endpointUri.Scheme != Uri.UriSchemeHttps))
                {
                    errors.Add($"{fieldPath}.endpoint must be a valid http or https absolute URL.");
                }
            }
            else if (type.Equals("database", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(locationConfig.ConnectionString))
                    errors.Add($"{fieldPath}.connectionString is required for database locations.");
            }
        }
    }

    private static void ValidateLocationWarnings(Dictionary<string, LocationConfig>? locations, List<string> warnings)
    {
        if (locations is null)
            return;

        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (locationName, locationConfig) in locations)
        {
            if (string.IsNullOrWhiteSpace(locationName))
            {
                warnings.Add("gateway.locations contains an empty location key.");
                continue;
            }

            var normalizedName = locationName.Trim();
            if (!seenNames.Add(normalizedName))
                warnings.Add($"gateway.locations contains duplicate location name '{normalizedName}' (case-insensitive).");

            if (locationConfig is null)
            {
                warnings.Add($"gateway.locations.{normalizedName} configuration is missing.");
                continue;
            }

            var type = string.IsNullOrWhiteSpace(locationConfig.Type)
                ? "filesystem"
                : locationConfig.Type.Trim();

            var fieldPath = $"gateway.locations.{normalizedName}";

            if (!TryValidateLocationType(type))
            {
                warnings.Add($"{fieldPath}.type is unknown '{type}'.");
                continue;
            }

            if (type.Equals("filesystem", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(locationConfig.Path))
                warnings.Add($"{fieldPath}.path is missing for filesystem location.");

            if ((type.Equals("api", StringComparison.OrdinalIgnoreCase)
                 || type.Equals("mcp-server", StringComparison.OrdinalIgnoreCase)
                 || type.Equals("remote-node", StringComparison.OrdinalIgnoreCase))
                && string.IsNullOrWhiteSpace(locationConfig.Endpoint))
                warnings.Add($"{fieldPath}.endpoint is missing for {type} location.");

            if (type.Equals("database", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(locationConfig.ConnectionString))
                warnings.Add($"{fieldPath}.connectionString is missing for database location.");
        }
    }

    private static bool TryValidateLocationType(string type)
        => type.Equals("filesystem", StringComparison.OrdinalIgnoreCase)
           || type.Equals("api", StringComparison.OrdinalIgnoreCase)
           || type.Equals("mcp-server", StringComparison.OrdinalIgnoreCase)
           || type.Equals("database", StringComparison.OrdinalIgnoreCase)
           || type.Equals("remote-node", StringComparison.OrdinalIgnoreCase);

    private static void ValidateCors(CorsConfig? cors, List<string> errors)
    {
        if (cors?.AllowedOrigins is null)
            return;

        for (var i = 0; i < cors.AllowedOrigins.Count; i++)
        {
            var origin = cors.AllowedOrigins[i];
            var field = $"gateway.cors.allowedOrigins[{i}]";

            if (string.IsNullOrWhiteSpace(origin))
            {
                errors.Add($"{field} must be a non-empty absolute URL.");
                continue;
            }

            if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri) ||
                (originUri.Scheme != Uri.UriSchemeHttp && originUri.Scheme != Uri.UriSchemeHttps))
            {
                errors.Add($"{field} must be a valid http or https absolute URL.");
            }
        }
    }

    private static void ValidateCron(CronConfig? cron, List<string> errors)
    {
        if (cron is null)
            return;

        if (cron.TickIntervalSeconds <= 0)
            errors.Add("cron.tickIntervalSeconds must be greater than zero.");

        if (cron.Jobs is null)
            return;

        foreach (var (jobId, job) in cron.Jobs)
        {
            if (string.IsNullOrWhiteSpace(jobId))
            {
                errors.Add("cron.jobs contains an empty job key. Use a stable job ID.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(job.Schedule))
                errors.Add($"cron.jobs.{jobId}.schedule is required.");

            if (string.IsNullOrWhiteSpace(job.ActionType))
                errors.Add($"cron.jobs.{jobId}.actionType is required.");

            if (string.Equals(job.ActionType, "agent-prompt", StringComparison.OrdinalIgnoreCase)
                || string.Equals(job.ActionType, "agent-chat", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(job.AgentId))
                    errors.Add($"cron.jobs.{jobId}.agentId is required for agent prompt jobs.");

                if (string.IsNullOrWhiteSpace(job.Message) && string.IsNullOrWhiteSpace(job.TemplateName))
                    errors.Add($"cron.jobs.{jobId} must define either message or templateName for agent prompt jobs.");
            }
        }
    }

    private static void ValidatePromptTemplates(
        IReadOnlyDictionary<string, PromptTemplateConfig>? templates,
        List<string> errors)
    {
        if (templates is null)
            return;

        foreach (var (templateName, template) in templates)
        {
            if (string.IsNullOrWhiteSpace(templateName))
            {
                errors.Add("promptTemplates contains an empty template key.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(template.Prompt))
                errors.Add($"promptTemplates.{templateName}.prompt is required.");
        }
    }
}
