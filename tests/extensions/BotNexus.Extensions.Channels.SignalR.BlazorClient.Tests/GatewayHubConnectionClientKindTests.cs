using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Covers the connect-time client-kind hint that the Blazor client appends to the hub URL
/// so the gateway can distinguish mobile from desktop SignalR connections (#1209, AC#1).
/// The append is isolated in a pure helper so the query-string composition -- including the
/// case where the hub URL already carries a query string -- is unit-testable without opening
/// a real SignalR connection.
/// </summary>
public sealed class GatewayHubConnectionClientKindTests
{
    [Fact]
    public void AppendClientKindQuery_NoExistingQuery_AppendsWithQuestionMark()
    {
        var result = GatewayHubConnection.AppendClientKindQuery("https://localhost:5000/hub/gateway", "mobile");

        result.ShouldBe("https://localhost:5000/hub/gateway?client=mobile");
    }

    [Fact]
    public void AppendClientKindQuery_ExistingQuery_AppendsWithAmpersand()
    {
        var result = GatewayHubConnection.AppendClientKindQuery("https://localhost:5000/hub/gateway?clientVersion=1.2.3", "mobile");

        result.ShouldBe("https://localhost:5000/hub/gateway?clientVersion=1.2.3&client=mobile");
    }

    [Fact]
    public void AppendClientKindQuery_Desktop_AppendsDesktop()
    {
        var result = GatewayHubConnection.AppendClientKindQuery("https://localhost:5000/hub/gateway", "desktop");

        result.ShouldBe("https://localhost:5000/hub/gateway?client=desktop");
    }

    [Fact]
    public void AppendClientKindQuery_NormalizesKindToLowercase()
    {
        var result = GatewayHubConnection.AppendClientKindQuery("https://localhost:5000/hub/gateway", "Mobile");

        result.ShouldBe("https://localhost:5000/hub/gateway?client=mobile");
    }

    [Fact]
    public void AppendClientKindQuery_WhitespaceKind_ReturnsUrlUnchanged()
    {
        var result = GatewayHubConnection.AppendClientKindQuery("https://localhost:5000/hub/gateway", "   ");

        result.ShouldBe("https://localhost:5000/hub/gateway");
    }

    [Fact]
    public void AppendClientKindQuery_NullKind_ReturnsUrlUnchanged()
    {
        var result = GatewayHubConnection.AppendClientKindQuery("https://localhost:5000/hub/gateway", null);

        result.ShouldBe("https://localhost:5000/hub/gateway");
    }
}
