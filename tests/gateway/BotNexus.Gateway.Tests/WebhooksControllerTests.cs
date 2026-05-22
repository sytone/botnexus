using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Gateway.Tests;

public sealed class WebhooksControllerTests
{
    private static WebhooksController MakeController(
        IAgentSupervisor? supervisor = null,
        IConversationStore? conversations = null,
        WebhookOptions? options = null)
    {
        var opts = options ?? new WebhookOptions
        {
            Enabled = true,
            Keys = [new WebhookKeyConfig { Id = "test", Key = "secret123" }]
        };
        var controller = new WebhooksController(
            supervisor ?? Mock.Of<IAgentSupervisor>(),
            conversations ?? Mock.Of<IConversationStore>(),
            Options.Create(opts));
        // Inject a fake HttpContext with the key header
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        return controller;
    }

    private static void SetWebhookKey(WebhooksController controller, string? key)
    {
        if (key is not null)
            controller.HttpContext.Request.Headers["X-BotNexus-Webhook-Key"] = key;
    }

    // ---- Auth tests ----

    [Fact]
    public async Task Message_WhenWebhooksDisabled_Returns401()
    {
        var controller = MakeController(options: new WebhookOptions { Enabled = false });
        SetWebhookKey(controller, "anykey");

        var result = await controller.Message(new WebhookMessageRequest("agent-a", "hello"), CancellationToken.None);

        result.ShouldBeOfType<UnauthorizedObjectResult>();
    }

    [Fact]
    public async Task Message_WhenNoKeyHeader_Returns401()
    {
        var controller = MakeController();
        // No key header set

        var result = await controller.Message(new WebhookMessageRequest("agent-a", "hello"), CancellationToken.None);

        result.ShouldBeOfType<UnauthorizedObjectResult>();
    }

    [Fact]
    public async Task Message_WhenInvalidKey_Returns401()
    {
        var controller = MakeController();
        SetWebhookKey(controller, "wrong-key");

        var result = await controller.Message(new WebhookMessageRequest("agent-a", "hello"), CancellationToken.None);

        result.ShouldBeOfType<UnauthorizedObjectResult>();
    }

    [Fact]
    public async Task Message_WhenKeyNotAuthorizedForAgent_Returns401()
    {
        var options = new WebhookOptions
        {
            Enabled = true,
            Keys = [new WebhookKeyConfig { Id = "scoped", Key = "secret123", AllowedAgents = ["agent-b"] }]
        };
        var controller = MakeController(options: options);
        SetWebhookKey(controller, "secret123");

        var result = await controller.Message(new WebhookMessageRequest("agent-a", "hello"), CancellationToken.None);

        result.ShouldBeOfType<UnauthorizedObjectResult>();
    }

    // ---- Validation tests ----

    [Fact]
    public async Task Message_WhenAgentIdMissing_Returns400()
    {
        var controller = MakeController();
        SetWebhookKey(controller, "secret123");

        var result = await controller.Message(new WebhookMessageRequest("", "hello"), CancellationToken.None);

        result.ShouldBeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Message_WhenMessageMissing_Returns400()
    {
        var controller = MakeController();
        SetWebhookKey(controller, "secret123");

        var result = await controller.Message(new WebhookMessageRequest("agent-a", ""), CancellationToken.None);

        result.ShouldBeOfType<BadRequestObjectResult>();
    }

    // ---- Happy path: new conversation ----

    [Fact]
    public async Task Message_WhenNoConversationId_CreatesNewConversationAndReturns202()
    {
        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "done" });

        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(It.IsAny<BotNexus.Domain.Primitives.AgentId>(), It.IsAny<BotNexus.Domain.Primitives.SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var convStore = new Mock<IConversationStore>();
        convStore.Setup(c => c.CreateAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Conversation conv, CancellationToken _) => conv);

        var controller = MakeController(supervisor: supervisor.Object, conversations: convStore.Object);
        SetWebhookKey(controller, "secret123");

        var result = await controller.Message(new WebhookMessageRequest("agent-a", "trigger me", null, "My Title"), CancellationToken.None);

        result.ShouldBeOfType<AcceptedResult>();
        convStore.Verify(c => c.CreateAsync(It.Is<Conversation>(conv => conv.Title == "My Title"), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ---- Happy path: existing conversation ----

    [Fact]
    public async Task Message_WithExistingConversationId_UsesExistingConversation()
    {
        var existing = new Conversation
        {
            ConversationId = BotNexus.Domain.Primitives.ConversationId.From("c_existing"),
            AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a"),
            Title = "Existing",
            Status = ConversationStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "done" });

        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(It.IsAny<BotNexus.Domain.Primitives.AgentId>(), It.IsAny<BotNexus.Domain.Primitives.SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var convStore = new Mock<IConversationStore>();
        convStore.Setup(c => c.GetAsync(BotNexus.Domain.Primitives.ConversationId.From("c_existing"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);

        var controller = MakeController(supervisor: supervisor.Object, conversations: convStore.Object);
        SetWebhookKey(controller, "secret123");

        var result = await controller.Message(new WebhookMessageRequest("agent-a", "inject me", "c_existing"), CancellationToken.None);

        result.ShouldBeOfType<AcceptedResult>();
        convStore.Verify(c => c.CreateAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---- Sad paths ----

    [Fact]
    public async Task Message_WhenConversationNotFound_Returns404()
    {
        var convStore = new Mock<IConversationStore>();
        convStore.Setup(c => c.GetAsync(It.IsAny<BotNexus.Domain.Primitives.ConversationId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Conversation?)null);

        var controller = MakeController(conversations: convStore.Object);
        SetWebhookKey(controller, "secret123");

        var result = await controller.Message(new WebhookMessageRequest("agent-a", "hello", "c_missing"), CancellationToken.None);

        result.ShouldBeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task Message_WhenKeyAuthorizedForAgent_AllowsRequest()
    {
        var options = new WebhookOptions
        {
            Enabled = true,
            Keys = [new WebhookKeyConfig { Id = "scoped", Key = "secret123", AllowedAgents = ["agent-a"] }]
        };

        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "done" });
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(It.IsAny<BotNexus.Domain.Primitives.AgentId>(), It.IsAny<BotNexus.Domain.Primitives.SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        var convStore = new Mock<IConversationStore>();
        convStore.Setup(c => c.CreateAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Conversation conv, CancellationToken _) => conv);

        var controller = MakeController(supervisor: supervisor.Object, conversations: convStore.Object, options: options);
        SetWebhookKey(controller, "secret123");

        var result = await controller.Message(new WebhookMessageRequest("agent-a", "hello"), CancellationToken.None);

        result.ShouldBeOfType<AcceptedResult>();
    }
}
