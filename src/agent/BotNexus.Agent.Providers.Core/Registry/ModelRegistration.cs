using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Providers.Core.Registry;

/// <summary>A model registration paired with its provider-instance key.</summary>
public sealed record ModelRegistration(string Provider, LlmModel Model);
