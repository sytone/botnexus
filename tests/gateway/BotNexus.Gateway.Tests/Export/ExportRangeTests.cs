using AngleSharp.Html.Parser;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Api.Export;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Tests.Export;

/// <summary>
/// Tests for the partial-range export contract (issue #3279), covering all seven acceptance
/// criteria: range selection (AC1), excerpt scope with recomputed totals (AC2), the omission note
/// (AC3), the four distinct rejections (AC4), boundary markers inside a range in both renderers
/// (AC5), redaction / sentinel / <c>NO_REPLY</c> parity seeded INSIDE the range (AC6), and the
/// single-entry and full-span shapes (AC7).
/// </summary>
public sealed class ExportRangeTests
{
    private const string Secret = "ghp_abcdefghijklmnopqrstuvwxyz0123456789";

    // ---------------------------------------------------------------- AC1

    [Fact]
    public async Task Range_ReturnsOnlyTheSelectedEntriesInAssembledOrder()
    {
        var (conversations, sessions, conversationId) = await SeedLinearAsync(6);
        var assembler = new ExportDocumentAssembler(conversations, sessions);
        var full = await assembler.AssembleConversationAsync(conversationId);
        full.ShouldNotBeNull();

        var range = RangeOver(full!, 1, 3);
        var result = await assembler.AssembleConversationRangeAsync(conversationId, range);

        result.IsSuccess.ShouldBeTrue(result.Message);
        var excerpt = result.Document!;
        excerpt.Entries.Count.ShouldBe(3);
        excerpt.Entries.Select(e => e.Content).ShouldBe(["m1", "m2", "m3"]);
        // Assembled order, not merely the right set.
        excerpt.Entries.Select(e => e.EntryId).ShouldBe(full!.Entries.Skip(1).Take(3).Select(e => e.EntryId));
    }

    [Fact]
    public async Task Range_OnSessionScope_ReturnsOnlyTheSelectedEntries()
    {
        var (conversations, sessions, _) = await SeedLinearAsync(5);
        var assembler = new ExportDocumentAssembler(conversations, sessions);
        var full = await assembler.AssembleSessionAsync(SessionId.From("s-1"));
        full.ShouldNotBeNull();

        var result = await assembler.AssembleSessionRangeAsync(SessionId.From("s-1"), RangeOver(full!, 2, 3));

        result.IsSuccess.ShouldBeTrue(result.Message);
        result.Document!.Entries.Select(e => e.Content).ShouldBe(["m2", "m3"]);
    }

    [Fact]
    public async Task NullRange_IsExactlyTheFullExport()
    {
        var (conversations, sessions, conversationId) = await SeedLinearAsync(4);
        var assembler = new ExportDocumentAssembler(conversations, sessions);

        var result = await assembler.AssembleConversationRangeAsync(conversationId, range: null);

        result.IsSuccess.ShouldBeTrue();
        result.Document!.Scope.ShouldBe(ExportScope.Conversation);
        result.Document.Range.ShouldBeNull();
        result.Document.OmissionNote.ShouldBeNull();
        result.Document.Entries.Count.ShouldBe(4);
    }

    // ---------------------------------------------------------------- AC2

    [Fact]
    public async Task Range_ReportsExcerptScopeAndRecomputesTotalsOverTheRange()
    {
        // The shipping bug this guards: reusing the full-conversation counts while swapping only the
        // entry list, producing an excerpt of three messages whose header claims the conversation's
        // total. Both totals are therefore asserted to DIFFER from the full document's.
        var conversationId = ConversationId.From("c_totals");
        var sessions = new InMemorySessionStore();
        var session = await sessions.GetOrCreateAsync(SessionId.From("s-1"), AgentId.From("quill"));
        session.Session.ConversationId = conversationId;
        for (var i = 0; i < 4; i++)
        {
            session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = $"q{i}", Timestamp = Ts(i * 2) });
            session.AddEntry(new SessionEntry
            {
                Role = MessageRole.Tool,
                ToolName = "read",
                ToolArgs = "{}",
                ToolCallId = $"tc-{i}",
                Content = string.Empty,
                Timestamp = Ts(i * 2 + 1)
            });
        }
        await sessions.SaveAsync(session);
        var conversations = new InMemoryConversationStore();
        await conversations.CreateAsync(NewConversation(conversationId, "Totals"));

        var assembler = new ExportDocumentAssembler(conversations, sessions);
        var full = await assembler.AssembleConversationAsync(conversationId);
        full!.MessageCount.ShouldBe(8);
        full.ToolCallCount.ShouldBe(4);

        // Entries 0..2 = q0, tool0, q1 -> 3 messages, 1 tool call.
        var result = await assembler.AssembleConversationRangeAsync(conversationId, RangeOver(full, 0, 2));

        result.IsSuccess.ShouldBeTrue(result.Message);
        var excerpt = result.Document!;
        excerpt.Scope.ShouldBe(ExportScope.Excerpt);
        excerpt.MessageCount.ShouldBe(3);
        excerpt.ToolCallCount.ShouldBe(1);
        excerpt.MessageCount.ShouldNotBe(full.MessageCount);
        excerpt.ToolCallCount.ShouldNotBe(full.ToolCallCount);
        excerpt.Sessions.Single().MessageCount.ShouldBe(3);
    }

    [Fact]
    public async Task Range_RecomputedTotalsAppearInBothRenderedHeaders()
    {
        // AC2 is about what the reader SEES, so pin it on the rendered output of both projections,
        // not only on the model.
        var (conversations, sessions, conversationId) = await SeedLinearAsync(10);
        var assembler = new ExportDocumentAssembler(conversations, sessions);
        var full = await assembler.AssembleConversationAsync(conversationId);
        var result = await assembler.AssembleConversationRangeAsync(conversationId, RangeOver(full!, 2, 4));
        result.IsSuccess.ShouldBeTrue(result.Message);

        var markdown = ExportMarkdownRenderer.Render(result.Document!);
        markdown.ShouldContain("- **Messages:** 3");
        markdown.ShouldNotContain("- **Messages:** 10");
        markdown.ShouldContain("# Transcript Excerpt");

        var html = ExportHtmlRenderer.Render(result.Document!);
        var parsed = new HtmlParser().ParseDocument(html);
        var text = parsed.DocumentElement.TextContent;
        text.ShouldContain("Messages");
        text.ShouldContain("excerpt");
        text.ShouldNotContain("10 message(s)");
    }

    // ---------------------------------------------------------------- AC3

    [Fact]
    public async Task Range_CarriesOmissionNoteInModelAndInBothRenderers()
    {
        var (conversations, sessions, conversationId) = await SeedLinearAsync(8);
        var assembler = new ExportDocumentAssembler(conversations, sessions);
        var full = await assembler.AssembleConversationAsync(conversationId);
        var result = await assembler.AssembleConversationRangeAsync(conversationId, RangeOver(full!, 3, 4));
        result.IsSuccess.ShouldBeTrue(result.Message);

        var excerpt = result.Document!;
        excerpt.OmittedEntryCount.ShouldBe(6);
        excerpt.OmissionNote.ShouldNotBeNullOrWhiteSpace();
        excerpt.OmissionNote!.ShouldContain("omitted");

        ExportMarkdownRenderer.Render(excerpt).ShouldContain(excerpt.OmissionNote);

        var html = ExportHtmlRenderer.Render(excerpt);
        var parsed = new HtmlParser().ParseDocument(html);
        parsed.DocumentElement.TextContent.ShouldContain("omitted");
    }

    [Fact]
    public async Task FullExport_CarriesNoOmissionNote()
    {
        // The complement: without this, a renderer that unconditionally printed the note would pass
        // the assertion above while lying on every ordinary export.
        var (conversations, sessions, conversationId) = await SeedLinearAsync(3);
        var document = await new ExportDocumentAssembler(conversations, sessions)
            .AssembleConversationAsync(conversationId);

        document!.OmissionNote.ShouldBeNull();
        document.OmittedEntryCount.ShouldBe(0);
        ExportMarkdownRenderer.Render(document).ShouldNotContain("omitted");
        new HtmlParser().ParseDocument(ExportHtmlRenderer.Render(document))
            .DocumentElement.TextContent.ShouldNotContain("omitted");
    }

    // ---------------------------------------------------------------- AC4

    [Fact]
    public async Task ReversedRange_IsRejectedWithItsOwnErrorAndNoDocument()
    {
        var (conversations, sessions, conversationId) = await SeedLinearAsync(5);
        var assembler = new ExportDocumentAssembler(conversations, sessions);
        var full = await assembler.AssembleConversationAsync(conversationId);

        var reversed = new ExportRangeSelector(full!.Entries[3].EntryId!, full.Entries[1].EntryId!);
        var result = await assembler.AssembleConversationRangeAsync(conversationId, reversed);

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(ExportRangeErrorKind.ReversedRange);
        result.ErrorCode.ShouldBe("range_reversed");
        // No partially-clamped document, and specifically not a silently swapped one.
        result.Document.ShouldBeNull();
    }

    [Fact]
    public async Task NonExistentEndpoint_IsRejectedWithItsOwnErrorAndNoDocument()
    {
        var (conversations, sessions, conversationId) = await SeedLinearAsync(5);
        var assembler = new ExportDocumentAssembler(conversations, sessions);
        var full = await assembler.AssembleConversationAsync(conversationId);

        var stale = new ExportRangeSelector(full!.Entries[0].EntryId!, ExportEntryId.Build(SessionId.From("s-1"), 999));
        var result = await assembler.AssembleConversationRangeAsync(conversationId, stale);

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(ExportRangeErrorKind.EndpointNotFound);
        result.ErrorCode.ShouldBe("range_endpoint_not_found");
        result.Document.ShouldBeNull();
    }

    [Fact]
    public async Task EndpointFromAnotherConversation_IsRejectedAsForeignAndNoDocument()
    {
        var (conversations, sessions, conversationId) = await SeedLinearAsync(4);

        // A second, genuinely separate conversation whose entry ids are perfectly well formed.
        var otherId = ConversationId.From("c_other");
        var other = await sessions.GetOrCreateAsync(SessionId.From("s-other"), AgentId.From("quill"));
        other.Session.ConversationId = otherId;
        other.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "elsewhere", Timestamp = Ts(0) });
        await sessions.SaveAsync(other);
        await conversations.CreateAsync(NewConversation(otherId, "Other"));

        var assembler = new ExportDocumentAssembler(conversations, sessions);
        var full = await assembler.AssembleConversationAsync(conversationId);
        var otherFull = await assembler.AssembleConversationAsync(otherId);

        var mixed = new ExportRangeSelector(full!.Entries[0].EntryId!, otherFull!.Entries[0].EntryId!);
        var result = await assembler.AssembleConversationRangeAsync(conversationId, mixed);

        result.IsSuccess.ShouldBeFalse();
        // Distinct from EndpointNotFound: the entry exists, just not here.
        result.Error.ShouldBe(ExportRangeErrorKind.ForeignConversation);
        result.ErrorCode.ShouldBe("range_endpoint_foreign_conversation");
        result.Document.ShouldBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-separator")]
    [InlineData("s-1#")]
    [InlineData("s-1#notanumber")]
    [InlineData("#3")]
    public async Task MalformedEndpoint_IsRejectedWithItsOwnError(string endpoint)
    {
        var (conversations, sessions, conversationId) = await SeedLinearAsync(3);
        var assembler = new ExportDocumentAssembler(conversations, sessions);
        var full = await assembler.AssembleConversationAsync(conversationId);

        var result = await assembler.AssembleConversationRangeAsync(
            conversationId, new ExportRangeSelector(full!.Entries[0].EntryId!, endpoint));

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(ExportRangeErrorKind.MalformedEndpoint);
        result.Document.ShouldBeNull();
    }

    [Fact]
    public async Task EveryRejectionKind_HasADistinctCodeAndMessage()
    {
        // The anti-collapse assertion for AC4: four failures, four codes, four messages. A future
        // simplification that folds them into one generic 400 reddens here rather than quietly
        // degrading the API.
        var (conversations, sessions, conversationId) = await SeedLinearAsync(4);
        var otherId = ConversationId.From("c_other2");
        var other = await sessions.GetOrCreateAsync(SessionId.From("s-other2"), AgentId.From("quill"));
        other.Session.ConversationId = otherId;
        other.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "elsewhere", Timestamp = Ts(0) });
        await sessions.SaveAsync(other);
        await conversations.CreateAsync(NewConversation(otherId, "Other"));

        var assembler = new ExportDocumentAssembler(conversations, sessions);
        var full = await assembler.AssembleConversationAsync(conversationId);
        var otherFull = await assembler.AssembleConversationAsync(otherId);
        var a = full!.Entries[0].EntryId!;

        var results = new[]
        {
            await assembler.AssembleConversationRangeAsync(conversationId, new ExportRangeSelector(full.Entries[2].EntryId!, a)),
            await assembler.AssembleConversationRangeAsync(conversationId, new ExportRangeSelector(a, ExportEntryId.Build(SessionId.From("s-1"), 999))),
            await assembler.AssembleConversationRangeAsync(conversationId, new ExportRangeSelector(a, otherFull!.Entries[0].EntryId!)),
            await assembler.AssembleConversationRangeAsync(conversationId, new ExportRangeSelector(a, "garbage"))
        };

        results.ShouldAllBe(r => !r.IsSuccess);
        results.Select(r => r.Error).Distinct().Count().ShouldBe(4);
        results.Select(r => r.ErrorCode).Distinct().Count().ShouldBe(4);
        results.Select(r => r.Message).Distinct().Count().ShouldBe(4);
    }

    [Fact]
    public async Task Route_InvalidRange_Returns400WithTheSpecificCode_AndValidRangeReturnsFile()
    {
        var (conversations, sessions, conversationId) = await SeedLinearAsync(5);
        var full = await new ExportDocumentAssembler(conversations, sessions)
            .AssembleConversationAsync(conversationId);
        var controller = new ConversationsController(conversations, sessions);

        var bad = await controller.ExportTranscript(
            conversationId.Value, "markdown", full!.Entries[3].EntryId, full.Entries[1].EntryId);

        var badRequest = bad.ShouldBeOfType<BadRequestObjectResult>();
        badRequest.Value!.ToString()!.ShouldContain("range_reversed");

        var good = await controller.ExportTranscript(
            conversationId.Value, "markdown", full.Entries[1].EntryId, full.Entries[3].EntryId);

        good.ShouldBeOfType<FileContentResult>();
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Route_HalfSpecifiedRange_IsRejectedRatherThanInferred(bool hasFirst, bool hasLast)
    {
        var (conversations, sessions, conversationId) = await SeedLinearAsync(4);
        var full = await new ExportDocumentAssembler(conversations, sessions)
            .AssembleConversationAsync(conversationId);
        var controller = new ConversationsController(conversations, sessions);

        var result = await controller.ExportTranscript(
            conversationId.Value,
            "markdown",
            hasFirst ? full!.Entries[0].EntryId : null,
            hasLast ? full!.Entries[2].EntryId : null);

        var badRequest = result.ShouldBeOfType<BadRequestObjectResult>();
        badRequest.Value!.ToString()!.ShouldContain("range_incomplete");
    }

    [Fact]
    public async Task Route_SessionScopeRangeRejection_AlsoReturns400()
    {
        var (conversations, sessions, _) = await SeedLinearAsync(4);
        var controller = new SessionsController(sessions, conversations: conversations);

        var result = await controller.ExportTranscript(
            "s-1", "html", ExportEntryId.Build(SessionId.From("s-1"), 0), ExportEntryId.Build(SessionId.From("s-1"), 42));

        var badRequest = result.ShouldBeOfType<BadRequestObjectResult>();
        badRequest.Value!.ToString()!.ShouldContain("range_endpoint_not_found");
    }

    // ---------------------------------------------------------------- AC5

    [Fact]
    public async Task BoundaryMarkerInsideRange_IsRenderedByBothRenderers()
    {
        var conversationId = ConversationId.From("c_boundary");
        var sessions = new InMemorySessionStore();

        var first = new GatewaySession(new Session
        {
            SessionId = SessionId.From("s-a"),
            ConversationId = conversationId,
            CreatedAt = Ts(0)
        });
        first.HydrateAgentId(AgentId.From("quill"));
        first.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "before-1", Timestamp = Ts(1) });
        first.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "before-2", Timestamp = Ts(2) });
        await sessions.SaveAsync(first);

        var second = new GatewaySession(new Session
        {
            SessionId = SessionId.From("s-b"),
            ConversationId = conversationId,
            CreatedAt = Ts(10)
        });
        second.HydrateAgentId(AgentId.From("quill"));
        second.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "after-1", Timestamp = Ts(11) });
        second.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "after-2", Timestamp = Ts(12) });
        await sessions.SaveAsync(second);

        var conversations = new InMemoryConversationStore();
        await conversations.CreateAsync(NewConversation(conversationId, "Boundary"));

        var assembler = new ExportDocumentAssembler(conversations, sessions);
        var full = await assembler.AssembleConversationAsync(conversationId);
        var boundaryIndex = full!.Entries.Select((e, i) => (e, i)).First(x => x.e.Kind == "boundary").i;

        // Straddle the boundary: one entry either side, boundary strictly inside.
        var result = await assembler.AssembleConversationRangeAsync(
            conversationId,
            new ExportRangeSelector(full.Entries[boundaryIndex - 1].EntryId!, full.Entries[boundaryIndex + 1].EntryId!));

        result.IsSuccess.ShouldBeTrue(result.Message);
        var excerpt = result.Document!;
        excerpt.Entries.Count(e => e.Kind == "boundary").ShouldBe(1);
        // Structural markers must not inflate the recomputed message total.
        excerpt.MessageCount.ShouldBe(2);

        ExportMarkdownRenderer.Render(excerpt).ShouldContain("Session boundary");

        var html = ExportHtmlRenderer.Render(excerpt);
        var parsed = new HtmlParser().ParseDocument(html);
        parsed.DocumentElement.TextContent.ShouldContain("Session boundary");
        parsed.QuerySelectorAll("hr.boundary").Length.ShouldBe(1);
    }

    // ---------------------------------------------------------------- AC6

    [Fact]
    public async Task RangedExport_AppliesRedactionToASecretSeededInsideTheRange()
    {
        // The seeded secret is at index 2 of a 5-entry transcript and the range is 1..3, so the
        // credential is strictly INSIDE the selection. A test that seeded it outside would pass
        // without proving redaction runs on the excerpt path at all.
        var conversationId = ConversationId.From("c_range_secret");
        var sessions = new InMemorySessionStore();
        var session = await sessions.GetOrCreateAsync(SessionId.From("s-1"), AgentId.From("quill"));
        session.Session.ConversationId = conversationId;
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "m0", Timestamp = Ts(0) });
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "m1", Timestamp = Ts(1) });
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = $"my token is {Secret}", Timestamp = Ts(2) });
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "m3", Timestamp = Ts(3) });
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "m4", Timestamp = Ts(4) });
        await sessions.SaveAsync(session);
        var conversations = new InMemoryConversationStore();
        await conversations.CreateAsync(NewConversation(conversationId, "Range secret"));

        var assembler = new ExportDocumentAssembler(conversations, sessions);
        var full = await assembler.AssembleConversationAsync(conversationId);
        var result = await assembler.AssembleConversationRangeAsync(conversationId, RangeOver(full!, 1, 3));
        result.IsSuccess.ShouldBeTrue(result.Message);
        var excerpt = result.Document!;

        // Anti-vacuity: the secret really is carried by the excerpt, so a green redaction assertion
        // below can only mean the redactor removed it.
        excerpt.Entries.ShouldContain(e => e.Content!.Contains(Secret, StringComparison.Ordinal));
        ExportMarkdownRenderer.Render(excerpt, redactSecrets: false).ShouldContain(Secret);
        ExportHtmlRenderer.Render(excerpt, redactSecrets: false).ShouldContain(Secret);

        ExportMarkdownRenderer.Render(excerpt).ShouldNotContain(Secret);

        var html = ExportHtmlRenderer.Render(excerpt);
        html.ShouldNotContain(Secret);
        var parsed = new HtmlParser().ParseDocument(html);
        parsed.DocumentElement.TextContent.ShouldNotContain(Secret);
    }

    [Fact]
    public async Task RangedExport_FiltersNoReplyAndSentinelsSeededInsideTheRange()
    {
        // Every filtered shape is seeded strictly between the two surviving anchors, so the range
        // provably spans them: the excerpt's own endpoints are the entries either side.
        var conversationId = ConversationId.From("c_range_filters");
        var sessions = new InMemorySessionStore();
        var session = await sessions.GetOrCreateAsync(SessionId.From("s-1"), AgentId.From("quill"));
        session.Session.ConversationId = conversationId;
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "outside-before", Timestamp = Ts(0) });
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "range-start", Timestamp = Ts(1) });
        session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "NO_REPLY", Timestamp = Ts(2) });
        session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "  NO_REPLY  ", Timestamp = Ts(3) });
        session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "crash placeholder", Timestamp = Ts(4), IsCrashSentinel = true });
        session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "", Timestamp = Ts(5) });
        session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "range-end", Timestamp = Ts(6) });
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "outside-after", Timestamp = Ts(7) });
        await sessions.SaveAsync(session);
        var conversations = new InMemoryConversationStore();
        await conversations.CreateAsync(NewConversation(conversationId, "Range filters"));

        var assembler = new ExportDocumentAssembler(conversations, sessions);
        var full = await assembler.AssembleConversationAsync(conversationId);

        var start = full!.Entries.Single(e => e.Content == "range-start");
        var end = full.Entries.Single(e => e.Content == "range-end");
        var result = await assembler.AssembleConversationRangeAsync(
            conversationId, new ExportRangeSelector(start.EntryId!, end.EntryId!));

        result.IsSuccess.ShouldBeTrue(result.Message);
        var excerpt = result.Document!;

        // The range's endpoints bracket the seeded positions, and the filtered rows are gone.
        excerpt.Entries.First().Content.ShouldBe("range-start");
        excerpt.Entries.Last().Content.ShouldBe("range-end");
        excerpt.Entries.Count.ShouldBe(2);
        excerpt.Entries.ShouldNotContain(e => e.Content!.Contains("NO_REPLY", StringComparison.Ordinal));
        excerpt.Entries.ShouldNotContain(e => e.Content == "crash placeholder");
        excerpt.Entries.ShouldNotContain(e => string.IsNullOrWhiteSpace(e.Content));
        // And the entries outside really were excluded, so the range is doing work.
        excerpt.Entries.ShouldNotContain(e => e.Content == "outside-before");
        excerpt.Entries.ShouldNotContain(e => e.Content == "outside-after");

        foreach (var rendered in new[] { ExportMarkdownRenderer.Render(excerpt), ExportHtmlRenderer.Render(excerpt) })
        {
            rendered.ShouldNotContain("NO_REPLY");
            rendered.ShouldNotContain("crash placeholder");
            rendered.ShouldContain("range-start");
            rendered.ShouldContain("range-end");
        }
    }

    [Fact]
    public async Task RangedExport_RedactsToolArgumentsAndToolResultsSeededInsideTheRange()
    {
        // Redaction covers more than message bodies; the excerpt path must not lose the tool-arg and
        // tool-result coverage the full export has.
        var conversationId = ConversationId.From("c_range_tools");
        var sessions = new InMemorySessionStore();
        var session = await sessions.GetOrCreateAsync(SessionId.From("s-1"), AgentId.From("quill"));
        session.Session.ConversationId = conversationId;
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "start", Timestamp = Ts(0) });
        session.AddEntry(new SessionEntry
        {
            Role = MessageRole.Tool,
            ToolName = "curl",
            ToolArgs = $"{{\"token\":\"{Secret}\"}}",
            ToolCallId = "tc-1",
            Content = string.Empty,
            Timestamp = Ts(1)
        });
        session.AddEntry(new SessionEntry
        {
            Role = MessageRole.Tool,
            ToolName = "curl",
            ToolCallId = "tc-1",
            Content = $"response contained {Secret}",
            Timestamp = Ts(2)
        });
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "end", Timestamp = Ts(3) });
        await sessions.SaveAsync(session);
        var conversations = new InMemoryConversationStore();
        await conversations.CreateAsync(NewConversation(conversationId, "Range tools"));

        var assembler = new ExportDocumentAssembler(conversations, sessions);
        var full = await assembler.AssembleConversationAsync(conversationId);
        var result = await assembler.AssembleConversationRangeAsync(conversationId, RangeOver(full!, 1, 2));
        result.IsSuccess.ShouldBeTrue(result.Message);
        var excerpt = result.Document!;

        excerpt.Entries.Count.ShouldBe(2);
        ExportMarkdownRenderer.Render(excerpt, redactSecrets: false).ShouldContain(Secret);
        ExportHtmlRenderer.Render(excerpt, redactSecrets: false).ShouldContain(Secret);
        ExportMarkdownRenderer.Render(excerpt).ShouldNotContain(Secret);
        ExportHtmlRenderer.Render(excerpt).ShouldNotContain(Secret);
    }

    [Fact]
    public async Task Route_RangedExport_RedactsByDefaultForBothFormats()
    {
        var conversationId = ConversationId.From("c_route_range_secret");
        var sessions = new InMemorySessionStore();
        var session = await sessions.GetOrCreateAsync(SessionId.From("s-1"), AgentId.From("quill"));
        session.Session.ConversationId = conversationId;
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "before", Timestamp = Ts(0) });
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = $"token {Secret}", Timestamp = Ts(1) });
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "after", Timestamp = Ts(2) });
        await sessions.SaveAsync(session);
        var conversations = new InMemoryConversationStore();
        await conversations.CreateAsync(NewConversation(conversationId, "Route range secret"));

        var full = await new ExportDocumentAssembler(conversations, sessions).AssembleConversationAsync(conversationId);
        var range = RangeOver(full!, 1, 1);
        var controller = new ConversationsController(conversations, sessions);

        foreach (var format in new[] { "markdown", "html" })
        {
            var result = await controller.ExportTranscript(
                conversationId.Value, format, range.FirstEntryId, range.LastEntryId);
            var file = result.ShouldBeOfType<FileContentResult>();
            var body = System.Text.Encoding.UTF8.GetString(file.FileContents);
            body.ShouldNotContain(Secret);
            body.ShouldContain("omitted");
        }
    }

    // ---------------------------------------------------------------- AC7

    [Fact]
    public async Task SingleEntryRange_ProducesAValidOneEntryDocumentInBothFormats()
    {
        var (conversations, sessions, conversationId) = await SeedLinearAsync(5);
        var assembler = new ExportDocumentAssembler(conversations, sessions);
        var full = await assembler.AssembleConversationAsync(conversationId);

        var result = await assembler.AssembleConversationRangeAsync(conversationId, RangeOver(full!, 2, 2));

        result.IsSuccess.ShouldBeTrue(result.Message);
        var excerpt = result.Document!;
        excerpt.Entries.Count.ShouldBe(1);
        excerpt.Entries[0].Content.ShouldBe("m2");
        excerpt.MessageCount.ShouldBe(1);
        excerpt.OmittedEntryCount.ShouldBe(4);

        var markdown = ExportMarkdownRenderer.Render(excerpt);
        markdown.ShouldContain("m2");
        markdown.ShouldNotContain("m0");
        markdown.ShouldNotContain("m4");

        var parsed = new HtmlParser().ParseDocument(ExportHtmlRenderer.Render(excerpt));
        parsed.QuerySelectorAll("article").Length.ShouldBe(1);
        parsed.DocumentElement.TextContent.ShouldContain("m2");
        parsed.DocumentElement.TextContent.ShouldNotContain("m0");
    }

    [Fact]
    public async Task FullSpanRange_MatchesUnrangedTranscriptContent_ButStaysExcerptScope()
    {
        // AC7's second half. Equivalence is asserted on TRANSCRIPT CONTENT only: the documents are
        // deliberately not equal, because the excerpt still declares itself a selection. Asserting
        // whole-document equality would force exactly the special-casing that hazard forbids.
        var (conversations, sessions, conversationId) = await SeedLinearAsync(6);
        var assembler = new ExportDocumentAssembler(conversations, sessions);
        var full = await assembler.AssembleConversationAsync(conversationId);

        var result = await assembler.AssembleConversationRangeAsync(
            conversationId,
            new ExportRangeSelector(full!.Entries[0].EntryId!, full.Entries[^1].EntryId!));

        result.IsSuccess.ShouldBeTrue(result.Message);
        var excerpt = result.Document!;

        excerpt.Entries
            .Select(e => (e.Kind, e.SessionId, e.Role, e.Content, e.Timestamp, e.EntryId))
            .ShouldBe(full.Entries.Select(e => (e.Kind, e.SessionId, e.Role, e.Content, e.Timestamp, e.EntryId)));
        excerpt.MessageCount.ShouldBe(full.MessageCount);
        excerpt.ToolCallCount.ShouldBe(full.ToolCallCount);
        excerpt.OmittedEntryCount.ShouldBe(0);

        // Still an excerpt: the caller supplied a range and the document says so.
        excerpt.Scope.ShouldBe(ExportScope.Excerpt);
        excerpt.Range.ShouldNotBeNull();
        excerpt.OmissionNote.ShouldNotBeNullOrWhiteSpace();

        // Rendered body entries match; only the header differs.
        var fullBody = TranscriptBody(ExportMarkdownRenderer.Render(full));
        var excerptBody = TranscriptBody(ExportMarkdownRenderer.Render(excerpt));
        excerptBody.ShouldBe(fullBody);
    }

    // ---------------------------------------------------------------- entry id contract

    [Fact]
    public async Task EntryIds_AreUniqueAndStableAgainstChangesInAnEarlierSession()
    {
        // The reason the selector uses {sessionId}#{ordinal} rather than a flat position: adding a
        // turn to an earlier session must not silently re-point a saved range at other entries.
        var conversationId = ConversationId.From("c_stable");
        var sessions = new InMemorySessionStore();

        var first = new GatewaySession(new Session
        {
            SessionId = SessionId.From("s-early"),
            ConversationId = conversationId,
            CreatedAt = Ts(0)
        });
        first.HydrateAgentId(AgentId.From("quill"));
        first.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "e0", Timestamp = Ts(1) });
        await sessions.SaveAsync(first);

        var second = new GatewaySession(new Session
        {
            SessionId = SessionId.From("s-late"),
            ConversationId = conversationId,
            CreatedAt = Ts(10)
        });
        second.HydrateAgentId(AgentId.From("quill"));
        second.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "l0", Timestamp = Ts(11) });
        second.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "l1", Timestamp = Ts(12) });
        await sessions.SaveAsync(second);

        var conversations = new InMemoryConversationStore();
        await conversations.CreateAsync(NewConversation(conversationId, "Stable"));

        var assembler = new ExportDocumentAssembler(conversations, sessions);
        var before = await assembler.AssembleConversationAsync(conversationId);
        before!.Entries.Select(e => e.EntryId).Distinct().Count().ShouldBe(before.Entries.Count);
        var pinned = before.Entries.Single(e => e.Content == "l1").EntryId!;

        var reloaded = await sessions.GetAsync(SessionId.From("s-early"));
        reloaded!.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "e1", Timestamp = Ts(2) });
        await sessions.SaveAsync(reloaded);

        var after = await assembler.AssembleConversationAsync(conversationId);
        after!.Entries.Single(e => e.EntryId == pinned).Content.ShouldBe("l1");

        var result = await assembler.AssembleConversationRangeAsync(
            conversationId, new ExportRangeSelector(pinned, pinned));
        result.IsSuccess.ShouldBeTrue(result.Message);
        result.Document!.Entries.Single().Content.ShouldBe("l1");
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Returns everything after the markdown header rule, i.e. the rendered transcript body with the
    /// summary header (which legitimately differs between an excerpt and a full export) removed.
    /// </summary>
    private static string TranscriptBody(string markdown)
    {
        const string separator = "\n---\n";
        var index = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).IndexOf(separator, StringComparison.Ordinal);
        return index < 0 ? markdown : markdown.Replace("\r\n", "\n", StringComparison.Ordinal)[(index + separator.Length)..];
    }

    private static ExportRangeSelector RangeOver(ExportDocument document, int firstIndex, int lastIndex)
        => new(document.Entries[firstIndex].EntryId!, document.Entries[lastIndex].EntryId!);

    private static async Task<(InMemoryConversationStore, InMemorySessionStore, ConversationId)> SeedLinearAsync(int count)
    {
        var conversationId = ConversationId.From("c_range");
        var sessions = new InMemorySessionStore();
        var session = await sessions.GetOrCreateAsync(SessionId.From("s-1"), AgentId.From("quill"));
        session.Session.ConversationId = conversationId;
        for (var i = 0; i < count; i++)
            session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = $"m{i}", Timestamp = Ts(i) });
        await sessions.SaveAsync(session);

        var conversations = new InMemoryConversationStore();
        await conversations.CreateAsync(NewConversation(conversationId, "Ranged"));

        return (conversations, sessions, conversationId);
    }

    private static DateTimeOffset Ts(int minutes)
        => new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(minutes);

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
