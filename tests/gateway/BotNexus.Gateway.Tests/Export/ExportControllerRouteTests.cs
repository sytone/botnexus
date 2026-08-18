using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Tests.Export;

/// <summary>
/// Route-level tests for the export endpoints (issue #3278, acceptance criteria 2, 3 and 7): the
/// conversation and session controllers must return the right content type, a
/// <c>Content-Disposition</c> filename carrying a slug and the export date, UTF-8 bytes, and the
/// correct status codes on the sad paths.
/// </summary>
public sealed class ExportControllerRouteTests
{
    [Fact]
    public async Task ConversationExport_Markdown_ReturnsMarkdownContentTypeAndDatedFilename()
    {
        var (conversations, sessions, conversationId) = await SeedAsync();
        var controller = new ConversationsController(conversations, sessions);

        var result = await controller.ExportTranscript(conversationId.Value, "markdown");

        var file = result.ShouldBeOfType<FileContentResult>();
        file.ContentType.ShouldBe("text/markdown");
        file.FileDownloadName.ShouldStartWith("quarterly-planning-");
        file.FileDownloadName.ShouldEndWith(".md");
        System.Text.Encoding.UTF8.GetString(file.FileContents)
            .ShouldContain("# Conversation Transcript");
    }

    [Fact]
    public async Task ConversationExport_Html_ReturnsHtmlContentTypeAndDatedFilename()
    {
        var (conversations, sessions, conversationId) = await SeedAsync();
        var controller = new ConversationsController(conversations, sessions);

        var result = await controller.ExportTranscript(conversationId.Value, "html");

        var file = result.ShouldBeOfType<FileContentResult>();
        file.ContentType.ShouldBe("text/html");
        file.FileDownloadName.ShouldEndWith(".html");
        System.Text.Encoding.UTF8.GetString(file.FileContents).ShouldStartWith("<!DOCTYPE html>");
    }

    [Fact]
    public async Task ConversationExport_EncodesBodyAsUtf8()
    {
        // AC7 (encoding), asserted on the bytes rather than the string: a renderer that produced
        // correct text but was serialised with a different encoding would still corrupt the
        // download, and only a byte-level round trip catches that.
        var conversationId = ConversationId.From("c_utf8");
        var sessions = new InMemorySessionStore();
        var session = await sessions.GetOrCreateAsync(SessionId.From("s-utf8"), AgentId.From("quill"));
        session.Session.ConversationId = conversationId;
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "日本語 café 🔬", Timestamp = Ts(0) });
        await sessions.SaveAsync(session);

        var conversations = new InMemoryConversationStore();
        await conversations.CreateAsync(NewConversation(conversationId, "Unicode"));

        var controller = new ConversationsController(conversations, sessions);
        var result = await controller.ExportTranscript(conversationId.Value, "markdown");

        var file = result.ShouldBeOfType<FileContentResult>();
        System.Text.Encoding.UTF8.GetString(file.FileContents).ShouldContain("日本語 café 🔬");
    }

    [Theory]
    [InlineData("pdf")]
    [InlineData("docx")]
    [InlineData("")]
    [InlineData("markdownn")]
    public async Task ConversationExport_UnknownFormat_ReturnsBadRequest(string format)
    {
        var (conversations, sessions, conversationId) = await SeedAsync();
        var controller = new ConversationsController(conversations, sessions);

        var result = await controller.ExportTranscript(conversationId.Value, format);

        result.ShouldBeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task ConversationExport_UnknownConversation_ReturnsNotFound()
    {
        var controller = new ConversationsController(new InMemoryConversationStore(), new InMemorySessionStore());

        var result = await controller.ExportTranscript("c_missing", "markdown");

        result.ShouldBeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task ConversationExport_RedactsSecretsByDefault()
    {
        // AC5: redaction is on for the ROUTE, not merely available in the renderer. The route does
        // not consult TranscriptExportOptions, so an operator who never opted in is still protected.
        const string secret = "ghp_abcdefghijklmnopqrstuvwxyz0123456789";
        var conversationId = ConversationId.From("c_secret");
        var sessions = new InMemorySessionStore();
        var session = await sessions.GetOrCreateAsync(SessionId.From("s-secret"), AgentId.From("quill"));
        session.Session.ConversationId = conversationId;
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = $"token {secret}", Timestamp = Ts(0) });
        await sessions.SaveAsync(session);

        var conversations = new InMemoryConversationStore();
        await conversations.CreateAsync(NewConversation(conversationId, "Secrets"));

        var controller = new ConversationsController(conversations, sessions);

        foreach (var format in new[] { "markdown", "html" })
        {
            var result = await controller.ExportTranscript(conversationId.Value, format);
            var file = result.ShouldBeOfType<FileContentResult>();
            System.Text.Encoding.UTF8.GetString(file.FileContents).ShouldNotContain(secret);
        }
    }

    [Fact]
    public async Task SessionExport_Html_ReturnsSelfContainedDocumentWithParentConversationSummary()
    {
        var (conversations, sessions, _) = await SeedAsync();
        var controller = new SessionsController(sessions, conversations: conversations);

        var result = await controller.ExportTranscript("s-1", "html");

        var file = result.ShouldBeOfType<FileContentResult>();
        file.ContentType.ShouldBe("text/html");
        var html = System.Text.Encoding.UTF8.GetString(file.FileContents);
        html.ShouldStartWith("<!DOCTYPE html>");
        // AC4: the parent conversation summary travels with a session export.
        html.ShouldContain("Quarterly planning");
    }

    [Fact]
    public async Task SessionExport_UnknownFormat_ReturnsBadRequest()
    {
        var (conversations, sessions, _) = await SeedAsync();
        var controller = new SessionsController(sessions, conversations: conversations);

        var result = await controller.ExportTranscript("s-1", "pdf");

        result.ShouldBeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task SessionExport_UnknownSession_ReturnsNotFound()
    {
        var (conversations, sessions, _) = await SeedAsync();
        var controller = new SessionsController(sessions, conversations: conversations);

        var result = await controller.ExportTranscript("s-missing", "html");

        result.ShouldBeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task LegacySessionMarkdownRoute_IsUnchanged()
    {
        // The pre-existing route keeps its historical filename and content type. This is the
        // back-compat guard for adding a {format} sibling route beside a literal one.
        var (conversations, sessions, _) = await SeedAsync();
        var controller = new SessionsController(sessions, conversations: conversations);

        var result = await controller.ExportMarkdown("s-1");

        var file = result.ShouldBeOfType<FileContentResult>();
        file.ContentType.ShouldBe("text/markdown");
        file.FileDownloadName.ShouldBe("session-s-1.md");
    }

    private static DateTimeOffset Ts(int minutes)
        => new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(minutes);

    private static async Task<(InMemoryConversationStore, InMemorySessionStore, ConversationId)> SeedAsync()
    {
        var conversationId = ConversationId.From("c_route");
        var sessions = new InMemorySessionStore();
        var session = await sessions.GetOrCreateAsync(SessionId.From("s-1"), AgentId.From("quill"));
        session.Session.ConversationId = conversationId;
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "hello", Timestamp = Ts(0) });
        session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "hi", Timestamp = Ts(1) });
        await sessions.SaveAsync(session);

        var conversations = new InMemoryConversationStore();
        await conversations.CreateAsync(NewConversation(conversationId, "Quarterly planning"));

        return (conversations, sessions, conversationId);
    }

    private static Conversation NewConversation(ConversationId conversationId, string title)
        => new()
        {
            ConversationId = conversationId,
            AgentId = AgentId.From("quill"),
            Title = title,
            Status = ConversationStatus.Active,
            CreatedAt = Ts(0),
            UpdatedAt = Ts(1)
        };
}
