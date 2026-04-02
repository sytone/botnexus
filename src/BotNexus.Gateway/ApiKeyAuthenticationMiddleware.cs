using System.Threading;
using BotNexus.Core.Configuration;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway;

public sealed class ApiKeyAuthenticationMiddleware
{
    private static readonly object _errorBody = new
    {
        error = "Unauthorized",
        message = "Invalid or missing API key."
    };

    private readonly RequestDelegate _next;
    private readonly ILogger<ApiKeyAuthenticationMiddleware> _logger;
    private readonly IOptionsMonitor<BotNexusConfig> _configMonitor;
    private int _missingApiKeyWarningLogged;

    public ApiKeyAuthenticationMiddleware(
        RequestDelegate next,
        IOptionsMonitor<BotNexusConfig> configMonitor,
        ILogger<ApiKeyAuthenticationMiddleware> logger)
    {
        _next = next;
        _logger = logger;
        _configMonitor = configMonitor;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var configuredApiKey = _configMonitor.CurrentValue.Gateway.ApiKey;
        var isApiKeyConfigured = !string.IsNullOrWhiteSpace(configuredApiKey);
        if (!isApiKeyConfigured)
        {
            if (Interlocked.CompareExchange(ref _missingApiKeyWarningLogged, 1, 0) == 0)
            {
                _logger.LogWarning(
                    "BotNexus:Gateway:ApiKey is not configured. Allowing unauthenticated gateway access.");
            }

            await _next(context).ConfigureAwait(false);
            return;
        }

        Interlocked.Exchange(ref _missingApiKeyWarningLogged, 0);

        var providedApiKey = context.Request.Headers["X-Api-Key"].ToString();
        if (string.IsNullOrWhiteSpace(providedApiKey))
            providedApiKey = context.Request.Query["apiKey"].ToString();

        if (!string.Equals(providedApiKey, configuredApiKey, StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(_errorBody, context.RequestAborted).ConfigureAwait(false);
            return;
        }

        await _next(context).ConfigureAwait(false);
    }
}
