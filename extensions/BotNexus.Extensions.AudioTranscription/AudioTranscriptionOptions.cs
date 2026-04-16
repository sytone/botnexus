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
