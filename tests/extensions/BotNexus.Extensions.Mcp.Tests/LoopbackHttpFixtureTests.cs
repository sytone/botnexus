using System.Net;
using System.Net.Sockets;
using System.Text;

namespace BotNexus.Extensions.Mcp.Tests;

public sealed class LoopbackHttpFixtureTests
{
    [Fact]
    public async Task Start_RetainsExclusiveOwnership_UntilDisposed()
    {
        Socket? owned = null;
        var fixture = new LoopbackHttpFixture(start: listener =>
        {
            owned = listener.Server;
            listener.Start();
            owned.IsBound.ShouldBeTrue();
            owned.SafeHandle.IsClosed.ShouldBeFalse();
        });
        var endpoint = fixture.Endpoint;
        try
        {
            using var contender = new TcpListener(endpoint);
            contender.ExclusiveAddressUse = true;
            Should.Throw<SocketException>(() => contender.Start());
        }
        finally
        {
            await fixture.DisposeAsync();
        }

        await fixture.DisposeAsync(); // Repeated disposal must not touch an already released listener.
        // Port reuse is unrelated to release: inspect the original owned socket.
        owned.ShouldNotBeNull();
        owned.SafeHandle.IsClosed.ShouldBeTrue();
        fixture.IsBound.ShouldBeFalse();
        fixture.Completion.IsCompleted.ShouldBeTrue();
        fixture.Completion.IsFaulted.ShouldBeFalse();
    }

    [Fact]
    public void Start_WhenSetupFailsAfterBind_ReleasesOwnedSocketAndPreservesFailure()
    {
        TcpListener? acquired = null;
        Socket? owned = null;
        var failure = new InvalidOperationException("injected setup failure");
        try
        {
            var thrown = Should.Throw<InvalidOperationException>(() => new LoopbackHttpFixture(start: listener =>
            {
                acquired = listener;
                owned = listener.Server;
                listener.Start();
                owned.IsBound.ShouldBeTrue();
                owned.SafeHandle.IsClosed.ShouldBeFalse();
                throw failure;
            }));
            thrown.ShouldBeSameAs(failure);
            owned.ShouldNotBeNull();
            owned.SafeHandle.IsClosed.ShouldBeTrue();
            acquired.ShouldNotBeNull();
            acquired.Server.IsBound.ShouldBeFalse();
        }
        finally
        {
            acquired?.Stop();
        }
    }

    [Fact]
    public async Task Dispose_WithIncompleteRequest_CancelsReadAndDrainsAcceptor()
    {
        var fixture = new LoopbackHttpFixture();
        using var connection = new TcpClient();
        try
        {
            await connection.ConnectAsync(fixture.Endpoint).WaitAsync(TimeSpan.FromSeconds(10));
            await connection.GetStream().WriteAsync(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\n"));
            await fixture.HeaderReadPending.WaitAsync(TimeSpan.FromSeconds(10));
            fixture.Completion.IsCompleted.ShouldBeFalse();
        }
        finally
        {
            await fixture.DisposeAsync();
        }

        fixture.Completion.IsCompleted.ShouldBeTrue();
        fixture.Completion.IsFaulted.ShouldBeFalse();
        // Accepted connections can retain TCP state after correct shutdown (notably on Windows).
        // Assert the listener released its socket, not that the endpoint can be rebound immediately.
        fixture.IsBound.ShouldBeFalse();
    }

    [Fact]
    public async Task Dispose_WhenRequestFails_ObservesFailureAndStillReleasesSocket()
    {
        var fixture = new LoopbackHttpFixture();
        try
        {
            using var connection = new TcpClient();
            await connection.ConnectAsync(fixture.Endpoint).WaitAsync(TimeSpan.FromSeconds(10));
            await fixture.Accepted.WaitAsync(TimeSpan.FromSeconds(10));
            connection.Client.Shutdown(SocketShutdown.Send);
            await Should.ThrowAsync<EndOfStreamException>(() => fixture.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            await Should.ThrowAsync<EndOfStreamException>(async () => await fixture.DisposeAsync());
        }
        fixture.Completion.IsFaulted.ShouldBeTrue();
        fixture.IsBound.ShouldBeFalse();
    }

    [Fact]
    public async Task Target_WhenDeliberatelyContacted_RecordsRequestAndAuthorization()
    {
        await using var target = new LoopbackHttpFixture();
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, target.Address);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer positive-control");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var response = await client.SendAsync(request, deadline.Token);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await target.DisposeAsync();
        target.RequestCount.ShouldBe(1);
        target.Authorization.ShouldBe("Bearer positive-control");
        target.Completion.IsCompletedSuccessfully.ShouldBeTrue();
        target.IsBound.ShouldBeFalse();
    }
}
