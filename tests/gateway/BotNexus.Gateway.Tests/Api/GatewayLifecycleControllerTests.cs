using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;

namespace BotNexus.Gateway.Tests.Api;

public sealed class GatewayLifecycleControllerTests
{
    [Fact]
    public async Task Shutdown_ReturnsAcceptedBeforeRequestingHostStop()
    {
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        var responseFeature = new RecordingResponseFeature();
        var features = new FeatureCollection();
        features.Set<Microsoft.AspNetCore.Http.Features.IHttpResponseFeature>(responseFeature);
        var controller = new GatewayController(Options.Create(new GatewayOptions()), lifetime)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext(features) }
        };

        var result = controller.Shutdown();

        result.ShouldBeOfType<AcceptedResult>();
        lifetime.DidNotReceive().StopApplication();
        await responseFeature.CompleteAsync();
        lifetime.Received(1).StopApplication();
    }

    private sealed class RecordingResponseFeature : Microsoft.AspNetCore.Http.Features.IHttpResponseFeature
    {
        private Func<object, Task>? _onCompleted;
        private object? _state;

        public int StatusCode { get; set; }
        public string? ReasonPhrase { get; set; }
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public Stream Body { get; set; } = Stream.Null;
        public bool HasStarted => false;
        public void OnStarting(Func<object, Task> callback, object state) { }
        public void OnCompleted(Func<object, Task> callback, object state)
        {
            _onCompleted = callback;
            _state = state;
        }

        public Task CompleteAsync() => _onCompleted?.Invoke(_state!) ?? Task.CompletedTask;
    }
}
