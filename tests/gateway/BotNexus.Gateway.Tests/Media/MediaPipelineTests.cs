using BotNexus.Gateway.Abstractions.Media;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Media;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests.Media;

public sealed class MediaPipelineTests
{
    [Fact]
    public async Task ProcessAsync_EmptyContentParts_ReturnsEmptyList()
    {
        var handler = CreateHandler("h1", 100, _ => true);
        var pipeline = CreatePipeline([handler.Object]);
        var context = CreateContext();

        var result = await pipeline.ProcessAsync([], context);

        result.ShouldBeEmpty();
        handler.Verify(h => h.CanHandle(It.IsAny<MessageContentPart>()), Times.Never);
        handler.Verify(h => h.ProcessAsync(It.IsAny<MessageContentPart>(), It.IsAny<MediaProcessingContext>()), Times.Never);
    }

    [Fact]
    public async Task ProcessAsync_NoHandlers_ReturnsContentPartsUnchanged()
    {
        var inputPart = CreateTextPart("hello");
        var pipeline = CreatePipeline([]);
        var context = CreateContext();

        var result = await pipeline.ProcessAsync([inputPart], context);

        result.Count().ShouldBe(1);
        result[0].ShouldBeSameAs(inputPart);
    }

    [Fact]
    public async Task ProcessAsync_HandlerCannotHandle_SkipsHandler()
    {
        var inputPart = CreateTextPart("hello");
        var handler = CreateHandler("h1", 100, _ => false);
        var pipeline = CreatePipeline([handler.Object]);
        var context = CreateContext();

        var result = await pipeline.ProcessAsync([inputPart], context);

        result.ShouldHaveSingleItem().ShouldBeSameAs(inputPart);
        handler.Verify(h => h.CanHandle(inputPart), Times.Once);
        handler.Verify(h => h.ProcessAsync(It.IsAny<MessageContentPart>(), It.IsAny<MediaProcessingContext>()), Times.Never);
    }

    [Fact]
    public async Task ProcessAsync_HandlerTransforms_ReturnsTransformedPart()
    {
        var inputPart = CreateTextPart("before");
        var transformedPart = CreateTextPart("after");
        var handler = CreateHandler(
            "transformer",
            100,
            _ => true,
            (part, _) => new MediaProcessingResult
            {
                ProcessedPart = transformedPart,
                WasTransformed = true
            });
        var pipeline = CreatePipeline([handler.Object]);
        var context = CreateContext();

        var result = await pipeline.ProcessAsync([inputPart], context);

        result.ShouldHaveSingleItem().ShouldBeSameAs(transformedPart);
    }

    [Fact]
    public async Task ProcessAsync_HandlerDoesNotTransform_ReturnsOriginalPart()
    {
        var inputPart = CreateTextPart("before");
        var differentPart = CreateTextPart("after");
        var handler = CreateHandler(
            "pass-through",
            100,
            _ => true,
            (part, _) => new MediaProcessingResult
            {
                ProcessedPart = differentPart,
                WasTransformed = false
            });
        var pipeline = CreatePipeline([handler.Object]);
        var context = CreateContext();

        var result = await pipeline.ProcessAsync([inputPart], context);

        result.ShouldHaveSingleItem().ShouldBeSameAs(inputPart);
    }

    [Fact]
    public async Task ProcessAsync_MultipleHandlers_ExecuteInPriorityOrder()
    {
        var audioPart = CreateBinaryPart("audio/wav", [1, 2, 3]);
        var transcribedTextPart = CreateTextPart("transcribed text");
        var context = CreateContext();

        var firstHandler = new Mock<IMediaHandler>(MockBehavior.Strict);
        firstHandler.SetupGet(h => h.Name).Returns("audio-transcriber");
        firstHandler.SetupGet(h => h.Priority).Returns(50);
        firstHandler.Setup(h => h.CanHandle(It.IsAny<MessageContentPart>()))
            .Returns((MessageContentPart part) => part is BinaryContentPart binary && binary.MimeType == "audio/wav");
        firstHandler.Setup(h => h.ProcessAsync(It.IsAny<MessageContentPart>(), It.IsAny<MediaProcessingContext>()))
            .ReturnsAsync((MessageContentPart _, MediaProcessingContext _) => new MediaProcessingResult
            {
                ProcessedPart = transcribedTextPart,
                WasTransformed = true
            });

        var secondHandler = new Mock<IMediaHandler>(MockBehavior.Strict);
        secondHandler.SetupGet(h => h.Name).Returns("text-normalizer");
        secondHandler.SetupGet(h => h.Priority).Returns(200);
        secondHandler.Setup(h => h.CanHandle(It.IsAny<MessageContentPart>()))
            .Returns((MessageContentPart part) => part is TextContentPart);
        secondHandler.Setup(h => h.ProcessAsync(It.IsAny<MessageContentPart>(), It.IsAny<MediaProcessingContext>()))
            .ReturnsAsync((MessageContentPart part, MediaProcessingContext _) => new MediaProcessingResult
            {
                ProcessedPart = part,
                WasTransformed = false
            });

        var pipeline = CreatePipeline([secondHandler.Object, firstHandler.Object]);

        var result = await pipeline.ProcessAsync([audioPart], context);

        result.ShouldHaveSingleItem().ShouldBeSameAs(transcribedTextPart);
        secondHandler.Verify(h => h.CanHandle(It.Is<MessageContentPart>(p => ReferenceEquals(p, transcribedTextPart))), Times.Once);
        firstHandler.Verify(h => h.ProcessAsync(It.Is<MessageContentPart>(p => ReferenceEquals(p, audioPart)), It.IsAny<MediaProcessingContext>()), Times.Once);
        secondHandler.Verify(h => h.ProcessAsync(It.Is<MessageContentPart>(p => ReferenceEquals(p, transcribedTextPart)), It.IsAny<MediaProcessingContext>()), Times.Once);
    }

    [Fact]
    public async Task ProcessAsync_HandlerThrowsException_PassesThroughOriginal()
    {
        var inputPart = CreateTextPart("hello");
        var handler = new Mock<IMediaHandler>();
        handler.SetupGet(h => h.Name).Returns("thrower");
        handler.SetupGet(h => h.Priority).Returns(100);
        handler.Setup(h => h.CanHandle(It.IsAny<MessageContentPart>())).Returns(true);
        handler.Setup(h => h.ProcessAsync(It.IsAny<MessageContentPart>(), It.IsAny<MediaProcessingContext>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        var pipeline = CreatePipeline([handler.Object]);
        var context = CreateContext();

        var result = await pipeline.ProcessAsync([inputPart], context);

        result.ShouldHaveSingleItem().ShouldBeSameAs(inputPart);
    }

    [Fact]
    public async Task ProcessAsync_HandlerThrows_StopsHandlerChainForThatPart()
    {
        var inputPart = CreateTextPart("hello");
        var first = new Mock<IMediaHandler>();
        first.SetupGet(h => h.Name).Returns("first");
        first.SetupGet(h => h.Priority).Returns(100);
        first.Setup(h => h.CanHandle(It.IsAny<MessageContentPart>())).Returns(true);
        first.Setup(h => h.ProcessAsync(It.IsAny<MessageContentPart>(), It.IsAny<MediaProcessingContext>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var second = new Mock<IMediaHandler>();
        second.SetupGet(h => h.Name).Returns("second");
        second.SetupGet(h => h.Priority).Returns(200);
        second.Setup(h => h.CanHandle(It.IsAny<MessageContentPart>())).Returns(true);

        var pipeline = CreatePipeline([first.Object, second.Object]);
        var context = CreateContext();

        var result = await pipeline.ProcessAsync([inputPart], context);

        result.ShouldHaveSingleItem().ShouldBeSameAs(inputPart);
        second.Verify(h => h.CanHandle(It.IsAny<MessageContentPart>()), Times.Never);
        second.Verify(h => h.ProcessAsync(It.IsAny<MessageContentPart>(), It.IsAny<MediaProcessingContext>()), Times.Never);
    }

    [Fact]
    public async Task ProcessAsync_MixedContentParts_ProcessesEachIndependently()
    {
        var textPart = CreateTextPart("hello");
        var audioPart = CreateBinaryPart("audio/wav", [9, 8, 7]);
        var transformedAudio = CreateTextPart("audio as text");
        var handler = CreateHandler(
            "audio-handler",
            100,
            part => part is BinaryContentPart binary && binary.MimeType == "audio/wav",
            (part, _) => new MediaProcessingResult
            {
                ProcessedPart = transformedAudio,
                WasTransformed = true
            });
        var pipeline = CreatePipeline([handler.Object]);
        var context = CreateContext();

        var result = await pipeline.ProcessAsync([textPart, audioPart], context);

        result.Count().ShouldBe(2);
        result[0].ShouldBeSameAs(textPart);
        result[1].ShouldBeSameAs(transformedAudio);
    }

    [Fact]
    public async Task ProcessAsync_CancellationToken_PassedToHandlers()
    {
        var inputPart = CreateTextPart("hello");
        using var cts = new CancellationTokenSource();
        var context = CreateContext(cts.Token);
        CancellationToken observedToken = default;
        var handler = CreateHandler(
            "token-checker",
            100,
            _ => true,
            (part, ctx) =>
            {
                observedToken = ctx.CancellationToken;
                return new MediaProcessingResult
                {
                    ProcessedPart = part,
                    WasTransformed = false
                };
            });
        var pipeline = CreatePipeline([handler.Object]);

        await pipeline.ProcessAsync([inputPart], context);

        observedToken.ShouldBe(cts.Token);
    }

    private static MediaPipeline CreatePipeline(IEnumerable<IMediaHandler> handlers)
        => new(handlers, NullLogger<MediaPipeline>.Instance);

    private static MediaProcessingContext CreateContext(CancellationToken cancellationToken = default)
        => new()
        {
            SessionId = "session-1",
            ChannelType = "web",
            CancellationToken = cancellationToken
        };

    private static TextContentPart CreateTextPart(string text)
        => new()
        {
            MimeType = "text/plain",
            Text = text
        };

    private static BinaryContentPart CreateBinaryPart(string mimeType, byte[] data)
        => new()
        {
            MimeType = mimeType,
            Data = data
        };

    private static Mock<IMediaHandler> CreateHandler(
        string name,
        int priority,
        Func<MessageContentPart, bool> canHandle,
        Func<MessageContentPart, MediaProcessingContext, MediaProcessingResult>? process = null)
    {
        var handler = new Mock<IMediaHandler>();
        handler.SetupGet(h => h.Name).Returns(name);
        handler.SetupGet(h => h.Priority).Returns(priority);
        handler.Setup(h => h.CanHandle(It.IsAny<MessageContentPart>()))
            .Returns(canHandle);
        if (process is not null)
        {
            handler.Setup(h => h.ProcessAsync(It.IsAny<MessageContentPart>(), It.IsAny<MediaProcessingContext>()))
                .ReturnsAsync((MessageContentPart part, MediaProcessingContext ctx) => process(part, ctx));
        }
        return handler;
    }
}
