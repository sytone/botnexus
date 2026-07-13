using System.Text.Json.Nodes;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Symmetric redaction / restoration of secret fields in the platform config
/// document, plus a lossless deep-merge used on the save path.
///
/// The GET path replaces every secret with <see cref="Placeholder"/> so the UI
/// never sees real secrets. When the UI later PUTs a section back, it round-trips
/// those placeholders verbatim. Writing them would clobber the real on-disk
/// secret (#1955). This helper restores the existing on-disk value anywhere the
/// incoming value is still the placeholder, keeping redaction and restoration
/// symmetric (they walk the exact same field paths).
///
/// The deep-merge keeps existing keys that the incoming payload omits so that a
/// partial/typed save never drops channel subtrees such as telegram bots or
/// serviceBus queues (#1954).
/// </summary>
public static class ConfigSecretMerge
{
    /// <summary>The literal value written in place of every redacted secret.</summary>
    public const string Placeholder = "***";

    /// <summary>
    /// Redacts every secret-bearing field in the whole-config document in place.
    /// Keyed off the top-level section names (apiKey, gateway, providers).
    /// </summary>
    public static void Redact(JsonObject config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config["providers"] is JsonObject providers)
            RedactProviderSecrets(providers);
        if (config["apiKey"] is JsonValue)
            config["apiKey"] = Placeholder;
        if (config["gateway"] is JsonObject gateway)
            RedactGatewaySecrets(gateway);
    }

    /// <summary>
    /// Deep-merges <paramref name="incoming"/> onto <paramref name="target"/> in place.
    /// Object values are merged recursively; scalar and array values replace. Keys
    /// present in <paramref name="target"/> but absent from <paramref name="incoming"/>
    /// are preserved (this is what protects omitted channel subtrees).
    /// </summary>
    public static void DeepMerge(JsonObject target, JsonObject incoming)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(incoming);
        foreach (var (key, incomingValue) in incoming)
        {
            if (incomingValue is JsonObject incomingObj && target[key] is JsonObject targetObj)
            {
                DeepMerge(targetObj, incomingObj);
            }
            else
            {
                target[key] = incomingValue?.DeepClone();
            }
        }
    }

    /// <summary>
    /// Restores real secrets on the whole-config document. Anywhere
    /// <paramref name="target"/> still holds the <see cref="Placeholder"/>, the
    /// corresponding value from <paramref name="existing"/> is copied back. Walks
    /// exactly the same field paths as <see cref="Redact"/> so the two stay symmetric.
    /// </summary>
    public static void RestoreSecrets(JsonObject existing, JsonObject target)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(target);

        RestoreScalar(existing, target, "apiKey");

        if (target["providers"] is JsonObject targetProviders &&
            existing["providers"] is JsonObject existingProviders)
        {
            foreach (var (name, providerNode) in targetProviders)
            {
                if (providerNode is JsonObject provider &&
                    existingProviders[name] is JsonObject existingProvider)
                    RestoreScalar(existingProvider, provider, "apiKey");
            }
        }

        if (target["gateway"] is JsonObject targetGateway &&
            existing["gateway"] is JsonObject existingGateway)
            RestoreGatewaySecrets(existingGateway, targetGateway);
    }

    private static void RestoreGatewaySecrets(JsonObject existingGateway, JsonObject targetGateway)
    {
        if (targetGateway["apiKeys"] is JsonObject targetApiKeys &&
            existingGateway["apiKeys"] is JsonObject existingApiKeys)
        {
            foreach (var (name, apiKeyNode) in targetApiKeys)
            {
                if (apiKeyNode is JsonObject apiKeyObject &&
                    existingApiKeys[name] is JsonObject existingApiKey)
                    RestoreScalar(existingApiKey, apiKeyObject, "apiKey");
            }
        }

        if (targetGateway["sessionStore"] is JsonObject targetSessionStore &&
            existingGateway["sessionStore"] is JsonObject existingSessionStore)
            RestoreScalar(existingSessionStore, targetSessionStore, "connectionString");

        if (targetGateway["locations"] is JsonObject targetLocations &&
            existingGateway["locations"] is JsonObject existingLocations)
        {
            foreach (var (name, locationNode) in targetLocations)
            {
                if (locationNode is JsonObject location &&
                    existingLocations[name] is JsonObject existingLocation)
                    RestoreScalar(existingLocation, location, "connectionString");
            }
        }

        if (targetGateway["crossWorld"] is JsonObject targetCrossWorld &&
            existingGateway["crossWorld"] is JsonObject existingCrossWorld)
            RestoreCrossWorldSecrets(existingCrossWorld, targetCrossWorld);
    }

    private static void RestoreCrossWorldSecrets(JsonObject existingCrossWorld, JsonObject targetCrossWorld)
    {
        if (targetCrossWorld["peers"] is JsonObject targetPeers &&
            existingCrossWorld["peers"] is JsonObject existingPeers)
        {
            foreach (var (name, peerNode) in targetPeers)
            {
                if (peerNode is JsonObject peer &&
                    existingPeers[name] is JsonObject existingPeer)
                    RestoreScalar(existingPeer, peer, "apiKey");
            }
        }

        if (targetCrossWorld["inbound"] is JsonObject targetInbound &&
            targetInbound["apiKeys"] is JsonObject targetInboundKeys &&
            existingCrossWorld["inbound"] is JsonObject existingInbound &&
            existingInbound["apiKeys"] is JsonObject existingInboundKeys)
        {
            foreach (var key in targetInboundKeys.Select(static pair => pair.Key).ToArray())
            {
                if (IsPlaceholder(targetInboundKeys[key]) && existingInboundKeys[key] is JsonNode existingValue)
                    targetInboundKeys[key] = existingValue.DeepClone();
            }
        }
    }

    private static void RestoreScalar(JsonObject existing, JsonObject target, string field)
    {
        if (IsPlaceholder(target[field]) && existing[field] is JsonNode existingValue)
            target[field] = existingValue.DeepClone();
    }

    private static bool IsPlaceholder(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var s) && s == Placeholder;

    private static void RedactGatewaySecrets(JsonObject gateway)
    {
        if (gateway["apiKeys"] is JsonObject apiKeys)
        {
            foreach (var (_, apiKeyNode) in apiKeys)
            {
                if (apiKeyNode is JsonObject apiKeyObject && apiKeyObject["apiKey"] is JsonValue)
                    apiKeyObject["apiKey"] = Placeholder;
            }
        }

        if (gateway["sessionStore"] is JsonObject sessionStore && sessionStore["connectionString"] is JsonValue)
            sessionStore["connectionString"] = Placeholder;

        if (gateway["locations"] is JsonObject locations)
        {
            foreach (var (_, locationNode) in locations)
            {
                if (locationNode is JsonObject location && location["connectionString"] is JsonValue)
                    location["connectionString"] = Placeholder;
            }
        }

        if (gateway["crossWorld"] is JsonObject crossWorld)
            RedactCrossWorldSecrets(crossWorld);
    }

    private static void RedactCrossWorldSecrets(JsonObject crossWorld)
    {
        if (crossWorld["peers"] is JsonObject peers)
        {
            foreach (var (_, peerNode) in peers)
            {
                if (peerNode is JsonObject peer && peer["apiKey"] is JsonValue)
                    peer["apiKey"] = Placeholder;
            }
        }

        if (crossWorld["inbound"] is not JsonObject inbound || inbound["apiKeys"] is not JsonObject inboundApiKeys)
            return;

        foreach (var key in inboundApiKeys.Select(static pair => pair.Key).ToArray())
        {
            if (inboundApiKeys[key] is JsonValue)
                inboundApiKeys[key] = Placeholder;
        }
    }

    private static void RedactProviderSecrets(JsonObject providers)
    {
        foreach (var (_, providerNode) in providers)
        {
            if (providerNode is JsonObject provider && provider.ContainsKey("apiKey"))
                provider["apiKey"] = Placeholder;
        }
    }
}
