using System.Text.Json.Nodes;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// The single source of truth for the agent set, provider and model a <em>fresh</em> BotNexus
/// installation is created with.
/// </summary>
/// <remarks>
/// <para>
/// Issue #2636. <c>botnexus init</c> and the startup reconciler
/// (<see cref="PlatformAgentReconciliationService"/>) both have to answer "what provider and model
/// does a bootstrap agent get". Before this type they answered it independently: <c>init</c>
/// hard-coded <c>assistant</c> and the reconciler derived provider/model from whatever the
/// installation already had. Adding Trailguide to <c>init</c> as a second literal block would have
/// created a third answer, and the three would drift the moment any one changed.
/// </para>
/// <para>
/// So the fresh-install decision lives here, once, and the bundled-agent <em>shape</em> continues
/// to live in <see cref="BundledPlatformAgents"/>. Crucially the Trailguide entry produced here is
/// built by the same
/// <see cref="PlatformAgentReconciliationService.BuildEntry(BundledAgentDefinition, ValueTuple{string, string}?)"/>
/// the reconciler uses, so an entry written by <c>init</c> is field-identical to one the
/// reconciler would have inserted - which is what lets the gateway start after <c>init</c> without
/// a single config write.
/// </para>
/// </remarks>
public static class FreshInstallAgentDefaults
{
    /// <summary>Config key of the generic assistant a fresh install is created with.</summary>
    public const string DefaultAgentId = "assistant";

    /// <summary>Provider every fresh-install agent is created with.</summary>
    public const string DefaultProvider = "github-copilot";

    /// <summary>Model every fresh-install agent is created with.</summary>
    public const string DefaultModel = "gpt-4.1";

    /// <summary>
    /// Builds the complete <c>agents</c> block for a fresh installation: the generic assistant
    /// plus every bundled agent in <see cref="BundledPlatformAgents.All"/>.
    /// </summary>
    /// <param name="provider">Provider override; defaults to <see cref="DefaultProvider"/>.</param>
    /// <param name="model">Model override; defaults to <see cref="DefaultModel"/>.</param>
    /// <returns>A fresh, unshared <see cref="JsonObject"/> keyed by agent id.</returns>
    public static JsonObject CreateAgents(string? provider = null, string? model = null)
    {
        var resolved = (
            Provider: string.IsNullOrWhiteSpace(provider) ? DefaultProvider : provider,
            Model: string.IsNullOrWhiteSpace(model) ? DefaultModel : model);

        var agents = new JsonObject
        {
            [DefaultAgentId] = new JsonObject
            {
                ["provider"] = resolved.Provider,
                ["model"] = resolved.Model,
                ["enabled"] = true
            }
        };

        foreach (var definition in BundledPlatformAgents.All)
            agents[definition.AgentId] = PlatformAgentReconciliationService.BuildEntry(definition, resolved);

        return agents;
    }

    /// <summary>
    /// Builds only the bundled-agent entries for a fresh installation, keyed by agent id.
    /// </summary>
    /// <remarks>
    /// Used by <c>init</c>, which serialises the generic assistant through the typed
    /// <c>PlatformConfig</c> model and only needs the raw JSON for the bundled agents, whose
    /// templates carry fields the typed model does not express.
    /// </remarks>
    public static JsonObject CreateBundledAgents(string? provider = null, string? model = null)
    {
        var agents = CreateAgents(provider, model);
        agents.Remove(DefaultAgentId);
        return agents;
    }
}
