using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Keeps model identifiers out of the case-insensitive, colon-delimited framework key space.
/// Only provider chat capacity maps are opaque; all other configuration retains ordinary binding.
/// </summary>
internal static class ExactModelCapacityConfiguration
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    internal static bool IsMapPath(string path)
    {
        var segments = path.Split('.');
        return segments.Length == 4
            && segments[0].Equals("providers", StringComparison.OrdinalIgnoreCase)
            && segments[2].Equals("chat", StringComparison.OrdinalIgnoreCase)
            && segments[3].Equals("modelCapacities", StringComparison.OrdinalIgnoreCase);
    }

    internal static Stream CreateBindingStream(ConfigDocument document)
    {
        var projection = document.Root.DeepClone().AsObject();
        foreach (var (_, chat) in Chats(projection))
        {
            var key = chat.Select(pair => pair.Key).FirstOrDefault(key =>
                key.Equals("modelCapacities", StringComparison.OrdinalIgnoreCase));
            if (key is not null)
                chat.Remove(key);
        }
        return new MemoryStream(Encoding.UTF8.GetBytes(projection.ToJsonString()));
    }

    internal static void Apply(IReadOnlyList<IConfigurationProvider> providers, PlatformConfig config)
    {
        if (config.Providers is null)
            return;

        // Maps are atomic configuration values (also in SQLite). Absent inherits; empty/null
        // suppresses; present replaces. Walk ALL accepted sources, not only the last raw document.
        foreach (var provider in providers)
        {
            if (provider is IAcceptedRawConfigDocumentProvider rawProvider)
            {
                var document = rawProvider.GetAcceptedRawDocument();
                if (document is null)
                    continue;
                var root = document.Root;
                foreach (var (providerId, chat) in Chats(root))
                {
                    var entry = chat.FirstOrDefault(pair =>
                        pair.Key.Equals("modelCapacities", StringComparison.OrdinalIgnoreCase));
                    if (entry.Key is not null && config.Providers.TryGetValue(providerId, out var target))
                    {
                        target.Chat ??= new ProviderChatConfig();
                        target.Chat.ModelCapacities = ParseMap(entry.Value?.ToJsonString());
                    }
                }
                continue;
            }

            foreach (var (providerId, target) in config.Providers)
            {
                var path = $"providers:{providerId}:chat:modelCapacities";
                if (provider.TryGet(path, out var wholeMap))
                {
                    target.Chat ??= new ProviderChatConfig();
                    target.Chat.ModelCapacities = ParseMap(wholeMap);
                }

                // Ordinary later-provider field overlays remain supported for unambiguous IDs.
                // Exact IDs containing separators or case-only siblings require a whole-map JSON
                // value, because environment/command-line providers themselves cannot represent them.
                foreach (var modelId in provider.GetChildKeys([], path).Distinct(StringComparer.Ordinal))
                {
                    var modelPath = ConfigurationPath.Combine(path, modelId);
                    var hasContext = provider.TryGet(modelPath + ":contextWindow", out var context);
                    var hasOutput = provider.TryGet(modelPath + ":maxTokens", out var output);
                    if (!hasContext && !hasOutput)
                        continue;
                    target.Chat ??= new ProviderChatConfig();
                    if (!target.Chat.ModelCapacities.TryGetValue(modelId, out var capacity))
                    {
                        capacity = new ProviderModelCapacityConfig();
                        target.Chat.ModelCapacities[modelId] = capacity;
                    }
                    if (hasContext)
                        capacity.ContextWindow = ParseInteger(context);
                    if (hasOutput)
                        capacity.MaxTokens = ParseInteger(output);
                }
            }
        }
    }

    private static int? ParseInteger(string? value) => string.IsNullOrEmpty(value)
        ? null : int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);

    private static Dictionary<string, ProviderModelCapacityConfig> ParseMap(string? json) =>
        string.IsNullOrEmpty(json) || json == "null"
            ? new(StringComparer.Ordinal)
            : JsonSerializer.Deserialize<Dictionary<string, ProviderModelCapacityConfig>>(json, Options)
                ?? new(StringComparer.Ordinal);

    private static IEnumerable<(string ProviderId, JsonObject Chat)> Chats(JsonObject root)
    {
        if (Find(root, "providers") is not JsonObject providers)
            yield break;
        foreach (var (providerId, node) in providers)
        {
            if (node is JsonObject provider && Find(provider, "chat") is JsonObject chat)
                yield return (providerId, chat);
        }
    }

    private static JsonNode? Find(JsonObject obj, string key) => obj.FirstOrDefault(pair =>
        pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;

}
