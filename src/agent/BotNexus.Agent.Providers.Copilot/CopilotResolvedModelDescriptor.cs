using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Providers.Copilot;

internal sealed record CopilotResolvedModelDescriptor(
    IReadOnlyList<string> AdvertisedEndpoints,
    bool SupportsResponsesWebSocket);

internal static class CopilotResolvedModelDescriptors
{
    private static readonly ConditionalWeakTable<LlmModel, CopilotResolvedModelDescriptor> s_descriptors = new();
    private static readonly ConcurrentDictionary<ModelKey, CopilotResolvedModelDescriptor> s_byIdentity = new();

    internal static void Set(LlmModel model, IReadOnlyList<string>? advertisedEndpoints)
    {
        var endpoints = advertisedEndpoints?.Where(static endpoint => !string.IsNullOrWhiteSpace(endpoint)).ToArray() ?? [];
        var descriptor = new CopilotResolvedModelDescriptor(
            endpoints,
            endpoints.Any(static endpoint => endpoint.Equals("ws:/responses", StringComparison.OrdinalIgnoreCase)));
        s_descriptors.Remove(model);
        s_descriptors.Add(model, descriptor);
        s_byIdentity[new ModelKey(model.Provider, model.Id, model.BaseUrl)] = descriptor;
    }

    internal static CopilotResolvedModelDescriptor Get(LlmModel model)
    {
        if (s_descriptors.TryGetValue(model, out var descriptor))
            return descriptor;
        return s_byIdentity.TryGetValue(new ModelKey(model.Provider, model.Id, model.BaseUrl), out descriptor)
            ? descriptor
            : new CopilotResolvedModelDescriptor([], false);
    }

    private readonly record struct ModelKey(string Provider, string Id, string BaseUrl);
}
