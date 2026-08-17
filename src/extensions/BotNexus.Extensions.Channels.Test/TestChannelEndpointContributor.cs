using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BotNexus.Extensions.Channels.Test;

/// <summary>
/// Hosts the HTTP surface an integration test drives the test channel through.
/// </summary>
/// <remarks>
/// <para>
/// Routes, all under <c>/test-channel</c>:
/// </para>
/// <list type="table">
///   <item><term>POST /test-channel/{channelId}/inbound</term><description>Inject an inbound message.</description></item>
///   <item><term>GET /test-channel/{channelId}/outbound</term><description>Poll captured deliveries.</description></item>
///   <item><term>DELETE /test-channel/{channelId}/outbound</term><description>Clear the capture queue.</description></item>
///   <item><term>GET /test-channel/logs</term><description>Read captured structured log entries.</description></item>
///   <item><term>DELETE /test-channel/logs</term><description>Clear the log buffer.</description></item>
/// </list>
/// <para>
/// <b>The endpoints are inert unless the adapter is loaded.</b> Every handler resolves the adapter
/// from the live <c>IChannelAdapter</c> set and returns 404 when it is absent, so even a host that
/// somehow mapped this contributor without the channel exposes no injection surface. That matters
/// more here than for the other channel contributors: an inbound endpoint that dispatches
/// unauthenticated messages into the gateway is exactly what must not exist in production, which is
/// why the manifest ships disabled AND the handlers fail closed.
/// </para>
/// </remarks>
public sealed class TestChannelEndpointContributor : IEndpointContributor
{
    /// <summary>Route prefix for every endpoint this contributor maps.</summary>
    public const string RoutePrefix = "/test-channel";

    /// <inheritdoc/>
    public void MapEndpoints(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost($"{RoutePrefix}/{{channelId}}/inbound", InjectInboundAsync)
            .WithName("TestChannelInbound")
            .ExcludeFromDescription();

        app.MapGet($"{RoutePrefix}/{{channelId}}/outbound", GetOutbound)
            .WithName("TestChannelOutbound")
            .ExcludeFromDescription();

        app.MapDelete($"{RoutePrefix}/{{channelId}}/outbound", ClearOutbound)
            .WithName("TestChannelClearOutbound")
            .ExcludeFromDescription();

        app.MapGet($"{RoutePrefix}/logs", GetLogs)
            .WithName("TestChannelLogs")
            .ExcludeFromDescription();

        app.MapDelete($"{RoutePrefix}/logs", ClearLogs)
            .WithName("TestChannelClearLogs")
            .ExcludeFromDescription();
    }

    /// <summary>
    /// Resolves the running test-channel adapter whose channel key matches <paramref name="channelId"/>.
    /// </summary>
    /// <remarks>
    /// Matching on the adapter's own <c>ChannelType</c> rather than trusting the route segment is
    /// what makes the configurable channel key safe: a request addressed to <c>telegram</c> reaches
    /// the test adapter only when the test adapter really IS registered as <c>telegram</c>, never by
    /// accident.
    /// </remarks>
    internal static TestChannelAdapter? ResolveAdapter(IServiceProvider services, string channelId)
        => services.GetServices<IChannelAdapter>()
            .OfType<TestChannelAdapter>()
            .FirstOrDefault(adapter =>
                string.Equals(adapter.ChannelType.Value, channelId, StringComparison.OrdinalIgnoreCase));

    private static async Task<IResult> InjectInboundAsync(
        string channelId,
        TestChannelInboundRequest request,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var logger = services.GetService<ILogger<TestChannelEndpointContributor>>();
        var adapter = ResolveAdapter(services, channelId);
        if (adapter is null)
            return Results.NotFound();

        if (request is null || string.IsNullOrWhiteSpace(request.Address))
            return Results.BadRequest(new { error = "address is required" });

        var dispatched = await adapter.InjectInboundAsync(
            request.Address,
            request.Content ?? string.Empty,
            request.SenderId,
            request.TargetAgentId,
            request.ConversationId,
            cancellationToken);

        if (dispatched)
            return Results.Accepted();

        // 409, not 202: the adapter exists but is not running, so the message was NOT dispatched.
        // Answering 202 here would turn a start-order defect into an unexplained test timeout.
        logger?.LogWarning(
            "Test channel '{ChannelId}' rejected an inbound injection because the adapter is not running",
            channelId);
        return Results.Conflict(new { error = "test channel adapter is not running" });
    }

    private static IResult GetOutbound(string channelId, string? address, IServiceProvider services)
    {
        var adapter = ResolveAdapter(services, channelId);
        if (adapter is null)
            return Results.NotFound();

        var records = string.IsNullOrWhiteSpace(address)
            ? adapter.GetAllOutbound()
            : adapter.GetOutbound(address);

        return Results.Ok(records);
    }

    private static IResult ClearOutbound(string channelId, string? address, IServiceProvider services)
    {
        var adapter = ResolveAdapter(services, channelId);
        if (adapter is null)
            return Results.NotFound();

        if (string.IsNullOrWhiteSpace(address))
        {
            var cleared = adapter.GetAllOutbound()
                .Select(record => record.Address)
                .Distinct(StringComparer.Ordinal)
                .Sum(adapter.ClearOutbound);
            return Results.Ok(new { cleared });
        }

        return Results.Ok(new { cleared = adapter.ClearOutbound(address) });
    }

    private static IResult GetLogs(IServiceProvider services)
    {
        var capture = services.GetService<TestChannelLogCapture>();
        if (capture is null)
            return Results.NotFound();

        // droppedEntryCount travels with the entries deliberately. A caller asserting that
        // something was NEVER logged cannot support that claim from a truncated window, and the
        // only way it can know the window truncated is if we tell it.
        return Results.Ok(new
        {
            entries = capture.Snapshot(),
            droppedEntryCount = capture.DroppedEntryCount,
            capacity = capture.Capacity,
        });
    }

    private static IResult ClearLogs(IServiceProvider services)
    {
        var capture = services.GetService<TestChannelLogCapture>();
        if (capture is null)
            return Results.NotFound();

        capture.Clear();
        return Results.NoContent();
    }
}

/// <summary>Request body for <c>POST /test-channel/{channelId}/inbound</c>.</summary>
/// <param name="Address">Channel address the message arrives on. Required.</param>
/// <param name="Content">Message text.</param>
/// <param name="SenderId">Optional channel-native sender token.</param>
/// <param name="TargetAgentId">Optional agent routing hint.</param>
/// <param name="ConversationId">Optional conversation routing hint.</param>
public sealed record TestChannelInboundRequest(
    string Address,
    string? Content,
    string? SenderId = null,
    string? TargetAgentId = null,
    string? ConversationId = null);
