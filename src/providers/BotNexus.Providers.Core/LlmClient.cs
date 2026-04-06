using System.Diagnostics;
using BotNexus.Providers.Core.Diagnostics;
using BotNexus.Providers.Core.Models;
using BotNexus.Providers.Core.Registry;
using BotNexus.Providers.Core.Streaming;

namespace BotNexus.Providers.Core;

/// <summary>
/// Top-level streaming functions. Port of pi-mono's stream.ts.
/// Resolves the API provider from the registry and delegates.
/// </summary>
public sealed class LlmClient
{
    public ApiProviderRegistry ApiProviders { get; }
    public ModelRegistry Models { get; }

    public LlmClient(ApiProviderRegistry apiProviderRegistry, ModelRegistry modelRegistry)
    {
        ApiProviders = apiProviderRegistry ?? throw new ArgumentNullException(nameof(apiProviderRegistry));
        Models = modelRegistry ?? throw new ArgumentNullException(nameof(modelRegistry));
    }

    public LlmStream Stream(LlmModel model, Context context, StreamOptions? options = null)
    {
        using var activity = ProviderDiagnostics.Source.StartActivity("llm.stream", ActivityKind.Client);
        TagActivity(activity, model);

        try
        {
            var provider = ResolveProvider(model.Api);
            var stream = provider.Stream(model, context, options);
            activity?.SetStatus(ActivityStatusCode.Ok);
            return stream;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    public async Task<AssistantMessage> CompleteAsync(
        LlmModel model, Context context, StreamOptions? options = null)
    {
        var stream = Stream(model, context, options);
        return await stream.GetResultAsync();
    }

    public LlmStream StreamSimple(LlmModel model, Context context, SimpleStreamOptions? options = null)
    {
        using var activity = ProviderDiagnostics.Source.StartActivity("llm.stream_simple", ActivityKind.Client);
        TagActivity(activity, model);

        try
        {
            var provider = ResolveProvider(model.Api);
            var stream = provider.StreamSimple(model, context, options);
            activity?.SetStatus(ActivityStatusCode.Ok);
            return stream;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    public async Task<AssistantMessage> CompleteSimpleAsync(
        LlmModel model, Context context, SimpleStreamOptions? options = null)
    {
        var stream = StreamSimple(model, context, options);
        return await stream.GetResultAsync();
    }

    private IApiProvider ResolveProvider(string api)
    {
        return ApiProviders.Get(api)
               ?? throw new InvalidOperationException($"No API provider registered for api: {api}");
    }

    private static void TagActivity(Activity? activity, LlmModel model)
    {
        activity?.SetTag("botnexus.provider.name", model.Provider);
        activity?.SetTag("botnexus.model", model.Id);
        activity?.SetTag("botnexus.model.api", model.Api);
    }
}
