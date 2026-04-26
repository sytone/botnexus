using BotNexus.Gateway.Abstractions.Media;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BotNexus.Extensions.AudioTranscription.Tests;

public sealed class WhisperTranscriptionHandlerTests
{
    [Fact]
    public async Task Metadata_UsesStableDefaults()
    {
        await using var handler = CreateHandler(new AudioTranscriptionOptions());

        handler.Name.ShouldBe("whisper-transcription");
        handler.Priority.ShouldBe(50);
    }

    [Fact]
    public async Task CanHandle_ReturnsTrueForSupportedMimeType_CaseInsensitive()
    {
        await using var handler = CreateHandler(new AudioTranscriptionOptions());

        var content = new BinaryContentPart
        {
            MimeType = "Audio/MP3",
            Data = [1, 2, 3]
        };

        handler.CanHandle(content).ShouldBeTrue();
    }

    [Fact]
    public async Task CanHandle_ReturnsFalseForUnsupportedMimeType()
    {
        await using var handler = CreateHandler(new AudioTranscriptionOptions());

        var content = new BinaryContentPart
        {
            MimeType = "image/png",
            Data = [1]
        };

        handler.CanHandle(content).ShouldBeFalse();
    }

    [Fact]
    public async Task CanHandle_ReturnsFalseForNonBinaryContent()
    {
        await using var handler = CreateHandler(new AudioTranscriptionOptions());

        var content = new TextContentPart
        {
            MimeType = "text/plain",
            Text = "hello"
        };

        handler.CanHandle(content).ShouldBeFalse();
    }

    [Fact]
    public async Task ProcessAsync_ReturnsOriginalPartForNonBinaryContent()
    {
        await using var handler = CreateHandler(new AudioTranscriptionOptions());
        var textPart = new TextContentPart
        {
            MimeType = "text/plain",
            Text = "no-op"
        };

        var result = await handler.ProcessAsync(textPart, CreateContext());

        result.ProcessedPart.ShouldBeSameAs(textPart);
        result.WasTransformed.ShouldBeFalse();
        result.Metadata.ShouldBeNull();
    }

    [Fact]
    public async Task ProcessAsync_ThrowsWhenModelPathIsMissing()
    {
        await using var handler = CreateHandler(
            new AudioTranscriptionOptions
            {
                ModelPath = "   "
            });
        var content = new BinaryContentPart
        {
            MimeType = "audio/wav",
            Data = [1, 2]
        };

        var exception = await Should.ThrowAsync<InvalidOperationException>(
            () => handler.ProcessAsync(content, CreateContext()));

        exception.Message.ShouldBe("Whisper model path is not configured.");
    }

    [Fact]
    public async Task ProcessAsync_ThrowsWhenModelFileDoesNotExist()
    {
        var missingModelPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.bin");
        await using var handler = CreateHandler(
            new AudioTranscriptionOptions
            {
                ModelPath = missingModelPath
            });
        var content = new BinaryContentPart
        {
            MimeType = "audio/wav",
            Data = [1, 2]
        };

        var exception = await Should.ThrowAsync<FileNotFoundException>(
            () => handler.ProcessAsync(content, CreateContext()));

        exception.Message.ShouldContain(missingModelPath);
    }

    [Fact]
    public async Task ProcessAsync_ThrowsFileNotFoundEvenWhenTokenIsCanceled()
    {
        var missingModelPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.bin");
        await using var handler = CreateHandler(
            new AudioTranscriptionOptions
            {
                ModelPath = missingModelPath
            });
        var content = new BinaryContentPart
        {
            MimeType = "audio/wav",
            Data = [1, 2]
        };
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var exception = await Should.ThrowAsync<FileNotFoundException>(
            () => handler.ProcessAsync(content, CreateContext(cts.Token)));

        exception.Message.ShouldContain(missingModelPath);
    }

    private static WhisperTranscriptionHandler CreateHandler(AudioTranscriptionOptions options)
        => new(Options.Create(options), NullLogger<WhisperTranscriptionHandler>.Instance);

    private static MediaProcessingContext CreateContext(CancellationToken cancellationToken = default)
        => new()
        {
            SessionId = "session-1",
            ChannelType = "cli",
            CancellationToken = cancellationToken
        };
}
