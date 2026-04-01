using Microsoft.AspNetCore.Http;

namespace BotNexus.Core.Abstractions;

/// <summary>Handles incoming webhook requests for a specific route path.</summary>
public interface IWebhookHandler
{
    /// <summary>Absolute route path for this webhook endpoint (for example, /webhooks/slack).</summary>
    string Path { get; }

    /// <summary>Processes the incoming webhook request.</summary>
    Task<IResult> HandleAsync(HttpContext context);
}
