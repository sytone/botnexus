using BotNexus.Agent.Tools;
using BotNexus.Core.Abstractions;
using BotNexus.Core.Models;
using FluentAssertions;
using Moq;

namespace BotNexus.Tests.Unit.Tests;

/// <summary>Tests for the enhanced <see cref="ToolRegistry"/>.</summary>
public class ToolRegistryTests
{
    private static ITool MakeTool(string name)
    {
        var mock = new Mock<ITool>();
        mock.Setup(t => t.Definition).Returns(new ToolDefinition(name, "desc", new Dictionary<string, ToolParameterSchema>()));
        mock.Setup(t => t.ExecuteAsync(It.IsAny<IReadOnlyDictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync($"result-{name}");
        return mock.Object;
    }

    [Fact]
    public void Register_AddsToolByName()
    {
        var registry = new ToolRegistry();
        registry.Register(MakeTool("my_tool"));
        registry.Contains("my_tool").Should().BeTrue();
    }

    [Fact]
    public void Register_ReplacesExistingToolWithSameName()
    {
        var registry = new ToolRegistry();
        registry.Register(MakeTool("tool"));
        var second = MakeTool("tool");
        registry.Register(second);
        registry.Get("tool").Should().BeSameAs(second);
    }

    [Fact]
    public void RegisterRange_AddsMultipleTools()
    {
        var registry = new ToolRegistry();
        registry.RegisterRange([MakeTool("a"), MakeTool("b"), MakeTool("c")]);
        registry.GetNames().Should().BeEquivalentTo(["a", "b", "c"]);
    }

    [Fact]
    public void Get_ReturnsNull_WhenNotFound()
    {
        var registry = new ToolRegistry();
        registry.Get("nonexistent").Should().BeNull();
    }

    [Fact]
    public void Remove_RemovesTool()
    {
        var registry = new ToolRegistry();
        registry.Register(MakeTool("to_remove"));
        registry.Remove("to_remove").Should().BeTrue();
        registry.Contains("to_remove").Should().BeFalse();
    }

    [Fact]
    public void Remove_ReturnsFalse_WhenNotFound()
    {
        var registry = new ToolRegistry();
        registry.Remove("ghost").Should().BeFalse();
    }

    [Fact]
    public void GetDefinitions_ReturnsAllDefinitions()
    {
        var registry = new ToolRegistry();
        registry.Register(MakeTool("x"));
        registry.Register(MakeTool("y"));
        registry.GetDefinitions().Should().HaveCount(2);
    }

    [Fact]
    public async Task ExecuteAsync_InvokesCorrectTool()
    {
        var registry = new ToolRegistry();
        registry.Register(MakeTool("calc"));
        var call = new ToolCallRequest("id1", "calc", new Dictionary<string, object?>());
        var result = await registry.ExecuteAsync(call);
        result.Should().Be("result-calc");
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsErrorString_WhenToolNotFound()
    {
        var registry = new ToolRegistry();
        var call = new ToolCallRequest("id1", "missing_tool", new Dictionary<string, object?>());
        var result = await registry.ExecuteAsync(call);
        result.Should().Contain("missing_tool").And.Contain("not found");
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsErrorString_WhenToolThrows()
    {
        var mock = new Mock<ITool>();
        mock.Setup(t => t.Definition).Returns(new ToolDefinition("boom", "throws", new Dictionary<string, ToolParameterSchema>()));
        mock.Setup(t => t.ExecuteAsync(It.IsAny<IReadOnlyDictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("kaboom"));

        var registry = new ToolRegistry();
        registry.Register(mock.Object);
        var result = await registry.ExecuteAsync(new ToolCallRequest("id", "boom", new Dictionary<string, object?>()));
        result.Should().Contain("kaboom");
    }
}
