namespace BotNexus.Agent.Providers.Core.Registry;

/// <summary>
/// A capability a provider <b>registration</b> declares its code implements (issue #2853).
///
/// <para>The unit that has capabilities is the registration, not the model and not the wire
/// format: <see cref="IApiProvider"/> is a chat-completion contract and cannot express "this
/// provider can also embed". Declaring the capability set alongside the registration lets a
/// lookup answer "which providers do embeddings" without widening a chat interface or standing
/// up a parallel registry.</para>
///
/// <para><b>Vocabulary only.</b> Like <c>SatelliteCapability</c>, these are declarations rather
/// than grants: nothing in the registry permits or refuses execution based on them today, and no
/// execution interface exists behind <see cref="Embeddings"/> yet. A member is added here only
/// once code can actually implement it -- speculative capabilities are how a vocabulary rots.</para>
/// </summary>
public enum ProviderCapability
{
    /// <summary>Chat completions -- the <see cref="IApiProvider.Stream"/> path. Every provider registered today declares this.</summary>
    Chat,

    /// <summary>Text embedding generation. Declared by a provider whose code can produce embedding vectors.</summary>
    Embeddings
}

/// <summary>
/// Shared, allocation-free capability sets so the common declarations are not re-allocated per
/// registration.
/// </summary>
public static class ProviderCapabilitySets
{
    /// <summary>
    /// The default declaration for a registration that does not state one: chat only. This
    /// preserves the pre-#2853 meaning of "registered provider" exactly -- everything in the
    /// registry today is a chat provider.
    /// </summary>
    public static IReadOnlySet<ProviderCapability> ChatOnly { get; } =
        new HashSet<ProviderCapability> { ProviderCapability.Chat };
}
