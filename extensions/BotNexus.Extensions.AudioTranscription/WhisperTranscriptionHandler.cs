using BotNexus.Gateway.Abstractions.Media;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whisper.net;

namespace BotNexus.Extensions.AudioTranscription;

/// <summary>
/// Media handler that transcribes audio content parts to text using Whisper.
/// </summary>
public sealed class WhisperTranscriptionHandler : IMediaHandler, IAsyncDisposable
{
    private readonly AudioTranscriptionOptions _options;
    private readonly ILogger<WhisperTranscriptionHandler> _logger;
    private readonly SemaphoreSlim _concurrencyGate;
    private WhisperFactory? _factory;
    private bool _initialized;
    private readonly Lock _initLock = new();

    public WhisperTranscriptionHandler(
        IOptions<AudioTranscriptionOptions> options,
        ILogger<WhisperTranscriptionHandler> logger)
    {
        _options = options.Value;
        _logger = logger;
        _concurrencyGate = new SemaphoreSlim(_options.MaxConcurrency, _options.MaxConcurrency);
    }

    public string Name => "whisper-transcription";
    public int Priority => 50;

    public bool CanHandle(MessageContentPart contentPart)
        => contentPart is BinaryContentPart binary
           && _options.SupportedMimeTypes.Contains(binary.MimeType, StringComparer.OrdinalIgnoreCase);

    public async Task<MediaProcessingResult> ProcessAsync(
        MessageContentPart contentPart,
        MediaProcessingContext context)
    {
        if (contentPart is not BinaryContentPart binary)
            return new MediaProcessingResult { ProcessedPart = contentPart };

        EnsureInitialized();

        await _concurrencyGate.WaitAsync(context.CancellationToken);
        try
        {
            _logger.LogInformation(
                "Transcribing {MimeType} audio ({Size} bytes) for session {SessionId}",
                binary.MimeType, binary.Data.Length, context.SessionId);

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            using var processor = _factory!.CreateBuilder()
                .WithLanguage(_options.Language)
                .Build();

            using var stream = new MemoryStream(binary.Data);
            var segments = new List<string>();

            await foreach (var segment in processor.ProcessAsync(stream, context.CancellationToken))
            {
                segments.Add(segment.Text);
            }

            var transcript = string.Join(" ", segments).Trim();
            stopwatch.Stop();

            _logger.LogInformation(
                "Transcription complete: {Duration}ms, {SegmentCount} segments, {CharCount} chars",
                stopwatch.ElapsedMilliseconds, segments.Count, transcript.Length);

            return new MediaProcessingResult
            {
                ProcessedPart = new TextContentPart
                {
                    MimeType = "text/plain",
                    Text = transcript
                },
                WasTransformed = true,
                Metadata = new Dictionary<string, object?>
                {
                    ["transcription.duration_ms"] = stopwatch.ElapsedMilliseconds,
                    ["transcription.segments"] = segments.Count,
                    ["transcription.original_mime"] = binary.MimeType,
                    ["transcription.original_size"] = binary.Data.Length,
                    ["transcription.model"] = _options.ModelPath
                }
            };
        }
        finally
        {
            _concurrencyGate.Release();
        }
    }

    private void EnsureInitialized()
    {
        if (_initialized) return;
        lock (_initLock)
        {
            if (_initialized) return;
            if (string.IsNullOrWhiteSpace(_options.ModelPath))
                throw new InvalidOperationException("Whisper model path is not configured.");
            if (!File.Exists(_options.ModelPath))
                throw new FileNotFoundException($"Whisper model not found: {_options.ModelPath}");

            _factory = WhisperFactory.FromPath(_options.ModelPath);
            _initialized = true;
            _logger.LogInformation("Whisper model loaded from {ModelPath}", _options.ModelPath);
        }
    }

    public ValueTask DisposeAsync()
    {
        _concurrencyGate.Dispose();
        _factory?.Dispose();
        return ValueTask.CompletedTask;
    }
}
