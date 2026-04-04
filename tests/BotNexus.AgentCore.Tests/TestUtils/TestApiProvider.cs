using BotNexus.Providers.Core;
using BotNexus.Providers.Core.Models;
using BotNexus.Providers.Core.Registry;
using BotNexus.Providers.Core.Streaming;

namespace BotNexus.AgentCore.Tests.TestUtils;

internal sealed class TestApiProvider : IApiProvider
{
    private readonly Func<LlmModel, Context, StreamOptions?, LlmStream> _streamFactory;
    private readonly Func<LlmModel, Context, SimpleStreamOptions?, LlmStream> _simpleStreamFactory;

    public TestApiProvider(
        string api,
        Func<LlmModel, Context, SimpleStreamOptions?, LlmStream>? simpleStreamFactory = null,
        Func<LlmModel, Context, StreamOptions?, LlmStream>? streamFactory = null)
    {
        Api = api;
        _simpleStreamFactory = simpleStreamFactory ?? ((_, _, _) => TestStreamFactory.CreateTextResponse("ok"));
        _streamFactory = streamFactory ?? ((model, context, options) => _simpleStreamFactory(model, context, options as SimpleStreamOptions));
    }

    public string Api { get; }

    public LlmStream Stream(LlmModel model, Context context, StreamOptions? options = null)
    {
        return _streamFactory(model, context, options);
    }

    public LlmStream StreamSimple(LlmModel model, Context context, SimpleStreamOptions? options = null)
    {
        return _simpleStreamFactory(model, context, options);
    }
}

internal sealed class ApiProviderScope(string sourceId) : IDisposable
{
    public void Dispose()
    {
        ApiProviderRegistry.Unregister(sourceId);
    }
}
