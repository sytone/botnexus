using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Streaming;

namespace BotNexus.Agent.Providers.Core.Registry;

/// <summary>
/// API provider contract. Each provider handles a specific API format
/// (e.g., "anthropic-messages", "openai-completions", "openai-responses").
/// </summary>
public interface IApiProvider
{
    string Api { get; }

    /// <summary>
    /// The behavioural contract this provider declares about itself (issue #2432). The agent loop
    /// reads this instead of running quirk workarounds speculatively against every provider.
    /// <para>
    /// Defaulted so that an out-of-tree extension provider, or a test double, keeps compiling and
    /// receives <see cref="ProviderCapabilities.Default"/> -- every quirk workaround OFF. A
    /// provider that needs one says so; nothing is inferred on its behalf.
    /// </para>
    /// </summary>
    ProviderCapabilities Capabilities => ProviderCapabilities.Default;

    LlmStream Stream(LlmModel model, Context context, StreamOptions? options = null);
    LlmStream StreamSimple(LlmModel model, Context context, SimpleStreamOptions? options = null);
}
