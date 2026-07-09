namespace BotNexus.Gateway.Sessions;

/// <summary>
/// Configuration for the session transcript export path (e.g. the markdown
/// export API). These settings only affect render-time output; they never
/// change what is persisted to the session store.
/// </summary>
public sealed class TranscriptExportOptions
{
    /// <summary>Section name in configuration.</summary>
    public const string SectionName = "transcriptExport";

    /// <summary>
    /// When true, exported transcripts are passed through <see cref="TranscriptSecretRedactor"/>
    /// so recognised credential shapes are replaced with a placeholder before the
    /// transcript leaves the process. Default: false, so export output stays
    /// byte-identical to historical behaviour unless an operator opts in.
    /// </summary>
    public bool RedactSecrets { get; set; }
}
