namespace BotNexus.Gateway.Configuration;

/// <summary>
/// The embedding backend an operator selected for memory retrieval (#2790).
/// </summary>
/// <remarks>
/// <para>
/// #2855 shipped the hosted-provider path behind a single <c>enabled</c> toggle, which encodes a
/// binary question ("embeddings on or off?") over what is really a three-way choice of backend.
/// That was adequate while exactly one backend existed; it stops being adequate the moment a
/// second one does, because "on" would then silently mean "whichever backend happens to be
/// compiled in". This enum makes the choice explicit and, critically, makes it OVERRIDABLE by a
/// single key regardless of which value ships as the default.
/// </para>
/// </remarks>
public enum MemoryEmbeddingBackend
{
    /// <summary>
    /// No generator is constructed. Retrieval is lexical-only (BM25 + temporal decay). This is
    /// the unconfigured default and a fully supported mode, not a failure state.
    /// </summary>
    None = 0,

    /// <summary>
    /// On-box inference over a local model artefact. Selectable and documented, but not satisfied
    /// by any runtime in this build - the ONNX Runtime native dependency is deliberately NOT
    /// vendored (#2790 criterion 7), so selecting it degrades to lexical-only with a warning until
    /// the local runtime lands.
    /// </summary>
    Local = 1,

    /// <summary>
    /// Reuses an already-configured platform provider's embeddings endpoint. Credentials come from
    /// the existing provider configuration rather than a second copy.
    /// </summary>
    Provider = 2,
}

/// <summary>Parsing for the operator-facing <c>backend</c> configuration value.</summary>
public static class MemoryEmbeddingBackendParser
{
    /// <summary>
    /// Parses the configured backend token. Returns <see langword="false"/> for a value that is
    /// present but unrecognised, so the caller can name the offending token in a warning instead
    /// of silently treating a typo as the default.
    /// </summary>
    /// <remarks>
    /// A blank or absent value is NOT a parse failure - it means "unspecified", which the
    /// configuration resolves from the legacy <c>enabled</c> toggle for backward compatibility.
    /// </remarks>
    public static bool TryParse(string? value, out MemoryEmbeddingBackend backend)
    {
        backend = MemoryEmbeddingBackend.None;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        switch (value.Trim().ToLowerInvariant())
        {
            case "none":
            case "off":
            case "disabled":
                backend = MemoryEmbeddingBackend.None;
                return true;
            case "local":
            case "onnx":
                backend = MemoryEmbeddingBackend.Local;
                return true;
            case "provider":
            case "hosted":
                backend = MemoryEmbeddingBackend.Provider;
                return true;
            default:
                return false;
        }
    }
}
