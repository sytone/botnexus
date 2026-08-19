using System.Text.Json;
using BotNexus.Agent.Core.Loop;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Core.Tests.Loop;

/// <summary>
/// Covers issue #3404: the remedy sentence appended to a truncation marker must be derived from the
/// DECLARED PARAMETERS of the tool that was actually invoked, never from a single unconditional
/// constant that tells the caller to turn a dial its console does not have.
/// </summary>
public class ToolOutputRemedyTests
{
    private static readonly string Oversized = new('x', 4096);

    /// <summary>
    /// AC2 (and the AC5 mutation target). A tool declaring <c>offset</c> and <c>limit</c> must get a
    /// marker naming those two parameters specifically.
    /// </summary>
    /// <remarks>
    /// AC5: restoring the unconditional <see cref="ToolOutputBudget.NarrowingGuidance"/> in
    /// <c>ToolOutputBudget.Apply</c> reddens THIS test by name, because it asserts both the presence
    /// of the named dials and the ABSENCE of the generic sentence - a mutation cannot satisfy it by
    /// emitting more text.
    /// </remarks>
    [Fact]
    public void Apply_ToolDeclaringOffsetAndLimit_NamesThoseParameters()
    {
        var text = Truncate(new SchemaTool("read", """{"type":"object","properties":{"path":{},"offset":{},"limit":{}}}"""));

        text.ShouldContain("`offset`");
        text.ShouldContain("`limit`");
        text.ShouldContain("read");
        text.ShouldNotContain(ToolOutputBudget.NarrowingGuidance);
    }

    /// <summary>
    /// AC1 negative half: a tool that declares no pagination parameter is never told to paginate,
    /// and no parameter name absent from its schema is quoted back at it.
    /// </summary>
    [Fact]
    public void Apply_ToolWithNoPaginationParameters_DoesNotAdvisePagination()
    {
        var text = Truncate(new SchemaTool("shell", """{"type":"object","properties":{"command":{},"timeout":{}}}"""));

        text.ShouldContain("declares no pagination parameters");
        text.ShouldNotContain("`offset`");
        text.ShouldNotContain("`limit`");
        text.ShouldNotContain("`$top`");
        text.ShouldNotContain("`$select`");
        text.ShouldNotContain("paginate");
    }

    /// <summary>
    /// AC3: the alternative offered to a non-paginating tool is one it can actually act on.
    /// </summary>
    [Fact]
    public void Apply_ToolWithNoPaginationParameters_OffersAnActionableAlternative()
    {
        var text = Truncate(new SchemaTool("exec", """{"type":"object","properties":{"command":{}}}"""));

        text.ShouldContain("continuation handle");
        text.ShouldContain("file");
    }

    /// <summary>
    /// AC4: an unparseable or non-object schema falls back to the generic sentence rather than
    /// throwing or emitting an empty remedy.
    /// </summary>
    [Theory]
    [InlineData("""{"type":"object"}""")]           // no properties member at all
    [InlineData("""{"type":"object","properties":[]}""")] // properties present but wrong shape
    [InlineData("""[1,2,3]""")]                      // not an object schema
    [InlineData("""null""")]
    public void Apply_UnreadableSchema_FallsBackToGenericSentence(string schema)
    {
        var text = Truncate(new SchemaTool("weird", schema));

        text.ShouldContain(ToolOutputBudget.NarrowingGuidance);
    }

    /// <summary>
    /// AC4: a null tool (no tool was resolved before truncation) also falls open, and does so
    /// without throwing.
    /// </summary>
    [Fact]
    public void Apply_NullTool_FallsBackToGenericSentence()
    {
        var text = Truncate(tool: null);

        text.ShouldContain(ToolOutputBudget.NarrowingGuidance);
    }

    /// <summary>
    /// AC4: a tool whose own property getter throws must not turn an oversized result into a crash.
    /// </summary>
    [Fact]
    public void Apply_ToolThrowingFromDefinition_FallsBackToGenericSentence()
    {
        var text = Truncate(new ThrowingTool());

        text.ShouldContain(ToolOutputBudget.NarrowingGuidance);
    }

    /// <summary>
    /// AC1: a tool declaring narrowing (not paging) parameters is advised to narrow, by name.
    /// </summary>
    [Fact]
    public void Apply_ToolDeclaringSelectOnly_AdvisesNarrowingByName()
    {
        var text = Truncate(new SchemaTool("graph_get", """{"type":"object","properties":{"$select":{},"url":{}}}"""));

        text.ShouldContain("`$select`");
        text.ShouldNotContain("page through");
    }

    /// <summary>
    /// AC6: deriving the remedy does not disturb the #2760 continuation handle - it is still present
    /// alongside the tool-specific sentence.
    /// </summary>
    [Fact]
    public void Apply_WithDerivedRemedy_StillCarriesContinuationHandle()
    {
        var store = new ToolOutputContinuationStore();
        var result = new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, Oversized)]);

        var bounded = ToolOutputBudget.Apply(
            result,
            512,
            store,
            new SchemaTool("read", """{"type":"object","properties":{"offset":{},"limit":{}}}"""));

        var text = string.Concat(bounded!.Content.Select(block => block.Value));
        text.ShouldContain(ToolOutputBudget.ContinuationToolName);
        text.ShouldContain("offset=512");
    }

    private static string Truncate(IAgentTool? tool)
    {
        var result = new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, Oversized)]);
        var bounded = ToolOutputBudget.Apply(result, 512, new ToolOutputContinuationStore(), tool);
        return string.Concat(bounded!.Content.Select(block => block.Value));
    }

    private sealed class SchemaTool(string name, string schemaJson) : IAgentTool
    {
        private readonly JsonElement schema = JsonDocument.Parse(schemaJson).RootElement.Clone();

        public string Name => name;

        public string Label => name;

        public Tool Definition => new(name, "test tool", this.schema);

        public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
            IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken = default)
            => Task.FromResult(arguments);

        public Task<AgentToolResult> ExecuteAsync(
            string toolCallId,
            IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken = default,
            AgentToolUpdateCallback? onUpdate = null)
            => Task.FromResult(new AgentToolResult([]));
    }

    private sealed class ThrowingTool : IAgentTool
    {
        public string Name => "throwing";

        public string Label => "Throwing";

        public Tool Definition => throw new InvalidOperationException("schema unavailable");

        public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
            IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken = default)
            => Task.FromResult(arguments);

        public Task<AgentToolResult> ExecuteAsync(
            string toolCallId,
            IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken = default,
            AgentToolUpdateCallback? onUpdate = null)
            => Task.FromResult(new AgentToolResult([]));
    }
}
