using BotNexus.Core.Models;
using BotNexus.Providers.Base;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BotNexus.Tests.Unit.Tests;

public class ProviderRetryTests
{
    private sealed class FakeProvider : LlmProviderBase
    {
        private int _callCount;
        private readonly int _failUntilAttempt;
        private readonly Exception? _alwaysThrow;

        public int CallCount => _callCount;

        public FakeProvider(int failUntilAttempt = 0, Exception? alwaysThrow = null, int maxRetries = 3)
            : base(NullLogger<FakeProvider>.Instance, maxRetries, TimeSpan.FromMilliseconds(1))
        {
            _failUntilAttempt = failUntilAttempt;
            _alwaysThrow = alwaysThrow;
        }

        public override string DefaultModel => "fake-model";

        protected override async Task<LlmResponse> ChatCoreAsync(ChatRequest request, CancellationToken cancellationToken)
        {
            _callCount++;
            if (_alwaysThrow is not null) throw _alwaysThrow;
            if (_callCount <= _failUntilAttempt) throw new HttpRequestException("Transient error");
            await Task.CompletedTask;
            return new LlmResponse("response", FinishReason.Stop);
        }

        public override async IAsyncEnumerable<StreamingChatChunk> ChatStreamAsync(ChatRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield return StreamingChatChunk.FromContentDelta("stream");
        }
    }

    private static ChatRequest MakeRequest() => new(
        [new ChatMessage("user", "hello")],
        new GenerationSettings());

    [Fact]
    public async Task ChatAsync_NoErrors_CallsOnce()
    {
        var provider = new FakeProvider();
        await provider.ChatAsync(MakeRequest());
        provider.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task ChatAsync_TransientError_RetriesAndSucceeds()
    {
        var provider = new FakeProvider(failUntilAttempt: 2, maxRetries: 3);
        var response = await provider.ChatAsync(MakeRequest());

        response.Content.Should().Be("response");
        provider.CallCount.Should().Be(3);
    }

    [Fact]
    public async Task ChatAsync_ExceedsMaxRetries_ThrowsException()
    {
        var provider = new FakeProvider(failUntilAttempt: 10, maxRetries: 2);

        var act = async () => await provider.ChatAsync(MakeRequest());

        await act.Should().ThrowAsync<HttpRequestException>();
        provider.CallCount.Should().Be(3); // initial + 2 retries
    }

    [Fact]
    public async Task ChatAsync_OperationCanceledException_DoesNotRetry()
    {
        var provider = new FakeProvider(alwaysThrow: new OperationCanceledException(), maxRetries: 3);

        var act = async () => await provider.ChatAsync(MakeRequest());

        await act.Should().ThrowAsync<OperationCanceledException>();
        provider.CallCount.Should().Be(1);
    }

    [Fact]
    public void ProviderRegistry_RegisterAndGet_Works()
    {
        var registry = new ProviderRegistry();
        var provider = new FakeProvider();

        registry.Register("fake", provider);

        registry.Get("fake").Should().Be(provider);
        registry.Get("FAKE").Should().Be(provider); // case-insensitive
    }

    [Fact]
    public void ProviderRegistry_GetRequired_ThrowsIfNotFound()
    {
        var registry = new ProviderRegistry();

        var act = () => registry.GetRequired("nonexistent");

        act.Should().Throw<InvalidOperationException>();
    }
}
