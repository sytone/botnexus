using BotNexus.Agent.Core.Hooks;

namespace BotNexus.CodingAgent.Extensions;

public sealed class ExtensionRunner(IReadOnlyList<IExtension> extensions)
{
    private readonly IReadOnlyList<IExtension> _extensions = extensions;
    public IReadOnlyList<IExtension> Extensions => _extensions;

    public async Task<BeforeToolCallResult?> OnToolCallAsync(
        ToolCallLifecycleContext context,
        CancellationToken cancellationToken = default)
    {
        BeforeToolCallResult? result = null;
        foreach (var extension in _extensions)
        {
            BeforeToolCallResult? current;
            try
            {
                current = await extension.OnToolCallAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogExtensionError(extension, nameof(OnToolCallAsync), ex);
                continue;
            }

            if (current?.Block == true)
            {
                return current;
            }

            if (current is not null)
            {
                result = current;
            }
        }

        return result;
    }

    public async Task<AfterToolCallResult?> OnToolResultAsync(
        ToolResultLifecycleContext context,
        CancellationToken cancellationToken = default)
    {
        AfterToolCallResult? result = null;
        foreach (var extension in _extensions)
        {
            AfterToolCallResult? current;
            try
            {
                current = await extension.OnToolResultAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogExtensionError(extension, nameof(OnToolResultAsync), ex);
                continue;
            }

            if (current is not null)
            {
                result = Merge(result, current);
            }
        }

        return result;
    }

    public async Task OnSessionStartAsync(SessionLifecycleContext context, CancellationToken cancellationToken = default)
    {
        foreach (var extension in _extensions)
        {
            try
            {
                await extension.OnSessionStartAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogExtensionError(extension, nameof(OnSessionStartAsync), ex);
            }
        }
    }

    public async Task OnSessionEndAsync(SessionLifecycleContext context, CancellationToken cancellationToken = default)
    {
        foreach (var extension in _extensions)
        {
            try
            {
                await extension.OnSessionEndAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogExtensionError(extension, nameof(OnSessionEndAsync), ex);
            }
        }
    }

    public async Task<string?> OnCompactionAsync(CompactionLifecycleContext context, CancellationToken cancellationToken = default)
    {
        foreach (var extension in _extensions)
        {
            string? summaryOverride;
            try
            {
                summaryOverride = await extension.OnCompactionAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogExtensionError(extension, nameof(OnCompactionAsync), ex);
                continue;
            }

            if (!string.IsNullOrWhiteSpace(summaryOverride))
                return summaryOverride;
        }

        return null;
    }

    public async Task<object> OnModelRequestAsync(
        object payload,
        BotNexus.Agent.Providers.Core.Models.LlmModel model,
        CancellationToken cancellationToken = default)
    {
        var currentPayload = payload;
        foreach (var extension in _extensions)
        {
            object? overridePayload;
            try
            {
                overridePayload = await extension
                    .OnModelRequestAsync(new ModelRequestLifecycleContext(currentPayload, model), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogExtensionError(extension, nameof(OnModelRequestAsync), ex);
                continue;
            }

            if (overridePayload is not null)
            {
                currentPayload = overridePayload;
            }
        }

        return currentPayload;
    }

    private static AfterToolCallResult Merge(AfterToolCallResult? current, AfterToolCallResult next)
    {
        if (current is null)
        {
            return next;
        }

        return new AfterToolCallResult(
            Content: next.Content ?? current.Content,
            Details: next.Details ?? current.Details,
            IsError: next.IsError ?? current.IsError);
    }

    private static void LogExtensionError(IExtension extension, string eventName, Exception ex)
    {
        Console.Error.WriteLine($"Extension '{extension.Name}' failed during {eventName}: {ex}");
    }
}
