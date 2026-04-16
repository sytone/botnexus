using BotNexus.Gateway.Abstractions.Media;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Diagnostics;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Media;

/// <summary>
/// Default media pipeline that routes content parts through registered handlers.
/// Handlers are matched by <see cref="IMediaHandler.CanHandle"/> and executed in
/// <see cref="IMediaHandler.Priority"/> order (lowest first).
/// </summary>
public sealed class MediaPipeline : IMediaPipeline
{
    private readonly IReadOnlyList<IMediaHandler> _handlers;
    private readonly ILogger<MediaPipeline> _logger;

    public MediaPipeline(IEnumerable<IMediaHandler> handlers, ILogger<MediaPipeline> logger)
    {
        _handlers = handlers.OrderBy(h => h.Priority).ToList();
        _logger = logger;
    }

    public async Task<IReadOnlyList<MessageContentPart>> ProcessAsync(
        IReadOnlyList<MessageContentPart> contentParts,
        MediaProcessingContext context)
    {
        if (contentParts.Count == 0)
            return contentParts;

        var results = new List<MessageContentPart>(contentParts.Count);

        foreach (var part in contentParts)
        {
            GatewayTelemetry.MediaPartsProcessed.Add(1,
                new KeyValuePair<string, object?>("botnexus.channel.type", context.ChannelType),
                new KeyValuePair<string, object?>("botnexus.session.id", context.SessionId));

            var processed = part;
            foreach (var handler in _handlers)
            {
                if (!handler.CanHandle(processed))
                    continue;

                try
                {
                    _logger.LogDebug(
                        "Processing {MimeType} content part with handler {HandlerName} (priority {Priority})",
                        processed.MimeType, handler.Name, handler.Priority);

                    var result = await handler.ProcessAsync(processed, context);

                    if (result.WasTransformed)
                    {
                        _logger.LogInformation(
                            "Handler {HandlerName} transformed {MimeType} content part",
                            handler.Name, processed.MimeType);
                        GatewayTelemetry.MediaPartsTransformed.Add(1,
                            new KeyValuePair<string, object?>("botnexus.channel.type", context.ChannelType),
                            new KeyValuePair<string, object?>("botnexus.session.id", context.SessionId),
                            new KeyValuePair<string, object?>("botnexus.media.handler.name", handler.Name));
                        processed = result.ProcessedPart;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Handler {HandlerName} failed processing {MimeType} content part. Passing through unchanged.",
                        handler.Name, processed.MimeType);
                    GatewayTelemetry.MediaHandlerErrors.Add(1,
                        new KeyValuePair<string, object?>("botnexus.channel.type", context.ChannelType),
                        new KeyValuePair<string, object?>("botnexus.session.id", context.SessionId),
                        new KeyValuePair<string, object?>("botnexus.media.handler.name", handler.Name));
                    break;
                }
            }

            results.Add(processed);
        }

        return results;
    }
}
