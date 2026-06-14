using BotNexus.Extensions.AudioTranscription;
using BotNexus.Gateway.Abstractions.Media;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BotNexus.Extensions.AudioTranscription.Tests;

/// <summary>
/// Verifies <see cref="WhisperTranscriptionHandler"/> enforces the configured
/// <see cref="AudioTranscriptionOptions.MaxAudioBytes"/> cap before buffering or invoking
/// Whisper, so an oversized audio payload cannot pin the (often single) transcription slot or
/// consume unbounded memory.
/// </summary>
/// <remarks>
/// The oversize-reject path returns before <c>EnsureInitialized()</c>, so it needs no real
/// Whisper model. For payloads that pass the size guard, the handler proceeds to initialise and
/// throws because no model is configured — we use that throw as positive proof the guard did not
/// short-circuit a legitimately-sized payload (running an actual transcription would require a
/// model binary we do not ship in tests).
/// </remarks>
public sealed class WhisperTranscriptionHandlerSizeLimitTests
{
    private static WhisperTranscriptionHandler CreateHandler(AudioTranscriptionOptions options)
        => new(Options.Create(options), NullLogger<WhisperTranscriptionHandler>.Instance);

    private static MediaProcessingContext Context() => new()
    {
        SessionId = "session-1",
        ChannelType = "test",
        CancellationToken = CancellationToken.None
    };

    private static BinaryContentPart Audio(int bytes) => new()
    {
        MimeType = "audio/wav",
        Data = new byte[bytes]
    };

    [Fact]
    public async Task ProcessAsync_PayloadAboveCap_IsSkippedWithoutInitialisingWhisper()
    {
        // ModelPath intentionally empty: if the guard let this through, EnsureInitialized would throw.
        var options = new AudioTranscriptionOptions { MaxAudioBytes = 1024, ModelPath = string.Empty };
        var handler = CreateHandler(options);

        var result = await handler.ProcessAsync(Audio(2048), Context());

        result.WasTransformed.ShouldBeFalse();
        result.ProcessedPart.ShouldBeOfType<BinaryContentPart>();
        result.Metadata.ShouldNotBeNull();
        result.Metadata!["transcription.skipped"].ShouldBe(true);
        result.Metadata!["transcription.skip_reason"].ShouldBe("audio_too_large");
        result.Metadata!["transcription.original_size"].ShouldBe(2048);
        result.Metadata!["transcription.max_bytes"].ShouldBe(1024L);
    }

    [Fact]
    public async Task ProcessAsync_PayloadExactlyAtCap_PassesSizeGuard()
    {
        // At-cap payload must NOT be skipped; with no model configured it proceeds to init and throws.
        var options = new AudioTranscriptionOptions { MaxAudioBytes = 1024, ModelPath = string.Empty };
        var handler = CreateHandler(options);

        var act = async () => await handler.ProcessAsync(Audio(1024), Context());

        // Reaching EnsureInitialized proves the size guard did not short-circuit the payload.
        await act.ShouldThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ProcessAsync_PayloadUnderCap_PassesSizeGuard()
    {
        var options = new AudioTranscriptionOptions { MaxAudioBytes = 1024, ModelPath = string.Empty };
        var handler = CreateHandler(options);

        var act = async () => await handler.ProcessAsync(Audio(512), Context());

        await act.ShouldThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ProcessAsync_CapDisabled_DoesNotSkipOversizedPayload()
    {
        // MaxAudioBytes = 0 disables the cap: a huge payload must pass the (absent) guard and
        // proceed to init (which throws because no model is configured) — never skipped.
        var options = new AudioTranscriptionOptions { MaxAudioBytes = 0, ModelPath = string.Empty };
        var handler = CreateHandler(options);

        var act = async () => await handler.ProcessAsync(Audio(10 * 1024 * 1024), Context());

        await act.ShouldThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ProcessAsync_NonBinaryPart_ReturnedUnchanged()
    {
        var options = new AudioTranscriptionOptions { MaxAudioBytes = 1024, ModelPath = string.Empty };
        var handler = CreateHandler(options);

        var text = new TextContentPart { MimeType = "text/plain", Text = "hello" };
        var result = await handler.ProcessAsync(text, Context());

        result.WasTransformed.ShouldBeFalse();
        result.ProcessedPart.ShouldBeSameAs(text);
    }

    [Fact]
    public void Defaults_MaxAudioBytes_Is25Mb()
    {
        var options = new AudioTranscriptionOptions();

        options.MaxAudioBytes.ShouldBe(25L * 1024 * 1024);
    }
}
