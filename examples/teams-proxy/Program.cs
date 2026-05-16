using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Core;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using BotNexus.TeamsProxy;
using BotNexus.TeamsProxy.Configuration;
using BotNexus.TeamsProxy.Models;
using BotNexus.TeamsProxy.Services;
using Microsoft.Extensions.Options;

JwtSecurityTokenHandler.DefaultMapInboundClaims = false;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

builder.Services.Configure<TeamsProxyOptions>(
    builder.Configuration.GetSection(TeamsProxyOptions.SectionName));

builder.Services.AddSingleton<TokenCredential>(serviceProvider =>
{
    var options = serviceProvider.GetRequiredService<IOptions<TeamsProxyOptions>>().Value;
    var environment = serviceProvider.GetRequiredService<IHostEnvironment>();

    if (!string.IsNullOrWhiteSpace(options.ManagedIdentityClientId))
    {
        return new ManagedIdentityCredential(
            ManagedIdentityId.FromUserAssignedClientId(options.ManagedIdentityClientId));
    }

    if (!environment.IsDevelopment())
    {
        throw new InvalidOperationException(
            "TeamsProxy:ManagedIdentityClientId must be configured outside Development.");
    }

    return new DefaultAzureCredential();
});

builder.Services.AddSingleton<ServiceBusClient>(serviceProvider =>
{
    var options = serviceProvider.GetRequiredService<IOptions<TeamsProxyOptions>>().Value;
    if (string.IsNullOrWhiteSpace(options.ServiceBusFullyQualifiedNamespace))
    {
        throw new InvalidOperationException(
            "TeamsProxy:ServiceBusFullyQualifiedNamespace must be configured.");
    }

    return new ServiceBusClient(
        options.ServiceBusFullyQualifiedNamespace,
        serviceProvider.GetRequiredService<TokenCredential>());
});

builder.Services.AddSingleton<ConversationContextStore>();
builder.Services.AddSingleton<ConnectorTokenProvider>();
builder.Services.AddSingleton<BotConnectorAuthValidator>();
builder.Services.AddSingleton<InboundQueuePublisher>();
builder.Services.AddHttpClient<BotConnectorClient>();

var teamsProxyOptions = builder.Configuration
    .GetSection(TeamsProxyOptions.SectionName)
    .Get<TeamsProxyOptions>() ?? new TeamsProxyOptions();
if (teamsProxyOptions.OutboundWorkerEnabled)
{
    builder.Services.AddHostedService<OutboundQueueWorker>();
}

builder.Services.AddHealthChecks();

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new
{
    service = "BotNexus Teams Proxy",
    messagingEndpoint = "/api/messages",
    health = "/healthz"
}));

app.MapHealthChecks("/healthz");

app.MapPost("/api/messages", async (
    HttpRequest request,
    BotConnectorAuthValidator authValidator,
    InboundQueuePublisher inboundQueue,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    var activity = await JsonSerializer.DeserializeAsync<BotActivity>(
        request.Body,
        JsonDefaults.Options,
        cancellationToken);

    if (activity is null)
    {
        logger.LogWarning("Rejected Bot Connector request because the activity body could not be deserialized.");
        return Results.BadRequest(new { error = "Request body must be a Bot Connector activity." });
    }

    logger.LogInformation(
        "Received Bot Connector activity {ActivityId} of type {ActivityType} from channel {ChannelId} with service URL host {ServiceUrlHost}.",
        activity.Id,
        activity.Type,
        activity.ChannelId,
        TryGetHost(activity.ServiceUrl));

    if (!string.Equals(activity.Type, "message", StringComparison.OrdinalIgnoreCase))
    {
        logger.LogWarning(
            "Rejected unsupported Bot Connector activity {ActivityId} of type {ActivityType}.",
            activity.Id,
            activity.Type);
        return Results.Problem(
            title: "Unsupported activity type",
            detail: "This queue bridge only supports asynchronous Teams message activities. Invoke activities require a synchronous Teams response and are intentionally rejected in v1.",
            statusCode: StatusCodes.Status501NotImplemented);
    }

    var validationError = activity.GetInboundValidationError();
    if (validationError is not null)
    {
        logger.LogWarning(
            "Rejected Bot Connector activity {ActivityId}: {ValidationError}.",
            activity.Id,
            validationError);
        return Results.BadRequest(new { error = validationError });
    }

    try
    {
        await authValidator.ValidateAsync(
            request.Headers.Authorization.ToString(),
            activity,
            cancellationToken);
    }
    catch (BotAuthenticationException exception)
    {
        logger.LogWarning(
            exception,
            "Rejected Bot Connector activity {ActivityId} because authentication failed.",
            activity.Id);
        return Results.Unauthorized();
    }

    var inboundMessage = BotNexusInboundMessage.FromActivity(activity);
    await inboundQueue.PublishAsync(inboundMessage, cancellationToken);

    logger.LogInformation(
        "Queued inbound BotNexus message {MessageId} for conversation {ConversationId}.",
        inboundMessage.MessageId,
        inboundMessage.ConversationId);

    return Results.Accepted(value: new
    {
        messageId = inboundMessage.MessageId,
        conversationId = inboundMessage.ConversationId
    });
});

app.Run();

static string? TryGetHost(string? serviceUrl)
{
    return Uri.TryCreate(serviceUrl, UriKind.Absolute, out var uri)
        ? uri.Host
        : null;
}
