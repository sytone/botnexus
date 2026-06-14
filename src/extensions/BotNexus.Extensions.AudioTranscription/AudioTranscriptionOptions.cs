namespace BotNexus.Extensions.AudioTranscription;

/// <summary>
/// Configuration options for audio transcription.
/// </summary>
public sealed class AudioTranscriptionOptions
{
    /// <summary>Path to the Whisper GGML model file.</summary>
    public string ModelPath { get; set; } = string.Empty;

    /// <summary>Language for transcription (default: "en").</summary>
    public string Language { get; set; } = "en";

    /// <summary>Maximum concurrent transcription operations.</summary>
    public int MaxConcurrency { get; set; } = 1;

    /// <summary>
    /// Maximum size, in bytes, of an audio payload this handler will transcribe.
    /// Whisper transcription is CPU-bound and buffers the entire payload in memory, so an
    /// oversized attachment can pin the (often single) concurrency slot for an unbounded duration
    /// and consume unbounded memory. Payloads larger than this are skipped before transcription.
    /// Defaults to 25 MB. A value of zero or less disables the limit (unbounded — not recommended).
    /// </summary>
    public long MaxAudioBytes { get; set; } = 25L * 1024 * 1024;

    /// <summary>MIME types this handler processes.</summary>
    public IReadOnlyList<string> SupportedMimeTypes { get; set; } =
    [
        "audio/wav",
        "audio/mpeg",
        "audio/mp3",
        "audio/ogg",
        "audio/webm",
        "audio/flac"
    ];
}
