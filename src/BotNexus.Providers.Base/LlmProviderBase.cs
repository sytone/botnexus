using BotNexus.Core.Abstractions;
using BotNexus.Core.Models;
using Microsoft.Extensions.Logging;

namespace BotNexus.Providers.Base;

/// <summary>
/// Abstract base class for LLM providers with built-in retry logic and logging.
/// </summary>
public abstract class LlmProviderBase : ILlmProvider
{
    protected readonly ILogger Logger;
    protected readonly int MaxRetries;
    protected readonly TimeSpan RetryDelay;

    protected LlmProviderBase(ILogger logger, int maxRetries = 3, TimeSpan? retryDelay = null)
    {
        Logger = logger;
        MaxRetries = maxRetries;
        RetryDelay = retryDelay ?? TimeSpan.FromSeconds(2);
    }

    /// <inheritdoc/>
    public abstract string DefaultModel { get; }

    /// <inheritdoc/>
    public GenerationSettings Generation { get; set; } = new();

    /// <inheritdoc/>
    public async Task<LlmResponse> ChatAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            try
            {
                return await ChatCoreAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (attempt < MaxRetries)
            {
                var delay = RetryDelay * Math.Pow(2, attempt);
                Logger.LogWarning(ex, "LLM request failed (attempt {Attempt}/{MaxRetries}), retrying in {Delay:F1}s",
                    attempt + 1, MaxRetries, delay.TotalSeconds);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }

        return await ChatCoreAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public abstract IAsyncEnumerable<string> ChatStreamAsync(ChatRequest request, CancellationToken cancellationToken = default);

    /// <summary>Performs the actual chat request. Override in derived classes.</summary>
    protected abstract Task<LlmResponse> ChatCoreAsync(ChatRequest request, CancellationToken cancellationToken);
}
