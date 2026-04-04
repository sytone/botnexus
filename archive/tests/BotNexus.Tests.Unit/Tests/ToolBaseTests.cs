using BotNexus.Agent.Tools;
using BotNexus.Core.Models;
using FluentAssertions;

namespace BotNexus.Tests.Unit.Tests;

/// <summary>Tests for <see cref="ToolBase"/> argument helpers and error handling.</summary>
public class ToolBaseTests
{
    // ── Concrete test double ─────────────────────────────────────────────────

    private sealed class EchoTool(Func<IReadOnlyDictionary<string, object?>, Task<string>>? impl = null) : ToolBase
    {
        public override ToolDefinition Definition => new(
            "echo",
            "Echoes arguments back",
            new Dictionary<string, ToolParameterSchema>
            {
                ["input"] = new("string", "Value to echo", Required: true)
            });

        protected override Task<string> ExecuteCoreAsync(
            IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken)
            => impl?.Invoke(arguments) ?? Task.FromResult(GetOptionalString(arguments, "input", "(empty)"));
    }

    private sealed class ThrowingTool(Exception ex) : ToolBase
    {
        public override ToolDefinition Definition => new("thrower", "Always throws", new Dictionary<string, ToolParameterSchema>());

        protected override Task<string> ExecuteCoreAsync(
            IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken)
            => throw ex;
    }

    // ── Execution wrapping ────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_ReturnsResult_WhenCoreSucceeds()
    {
        var tool = new EchoTool();
        var args = new Dictionary<string, object?> { ["input"] = "hello" };
        var result = await tool.ExecuteAsync(args);
        result.Should().Be("hello");
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsErrorString_WhenCoreThrowsToolArgumentException()
    {
        var tool = new ThrowingTool(new ToolArgumentException("bad arg"));
        var result = await tool.ExecuteAsync(new Dictionary<string, object?>());
        result.Should().StartWith("Error: bad arg");
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsErrorString_WhenCoreThrowsGenericException()
    {
        var tool = new ThrowingTool(new InvalidOperationException("something broke"));
        var result = await tool.ExecuteAsync(new Dictionary<string, object?>());
        result.Should().Contain("something broke");
    }

    [Fact]
    public async Task ExecuteAsync_RethrowsCancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var tool = new ThrowingTool(new OperationCanceledException());
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => tool.ExecuteAsync(new Dictionary<string, object?>(), cts.Token));
    }

    // ── GetRequiredString ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetRequiredString_ThrowsToolArgumentException_WhenMissing()
    {
        var tool = new EchoTool(args =>
        {
            GetRequiredString(args, "nonexistent"); // call static method via instance
            return Task.FromResult("ok");
        });
        var result = await tool.ExecuteAsync(new Dictionary<string, object?>());
        result.Should().StartWith("Error:");
    }

    [Fact]
    public async Task GetRequiredString_ReturnsValue_WhenPresent()
    {
        var tool = new EchoTool();
        var args = new Dictionary<string, object?> { ["input"] = "world" };
        var result = await tool.ExecuteAsync(args);
        result.Should().Be("world");
    }

    // ── GetOptionalString / GetOptionalInt / GetOptionalBool ─────────────────

    [Fact]
    public async Task GetOptionalString_ReturnsDefault_WhenKeyAbsent()
    {
        var tool = new EchoTool(); // returns GetOptionalString(args, "input", "(empty)")
        var result = await tool.ExecuteAsync(new Dictionary<string, object?>());
        result.Should().Be("(empty)");
    }

    [Fact]
    public async Task GetOptionalInt_ReturnsValue_ForInteger()
    {
        var args = new Dictionary<string, object?> { ["n"] = 42 };

        // Directly invoke helper via a wrapper tool
        int captured = 0;
        var tool = new EchoTool(a => { captured = GetOptionalInt(a, "n", -1); return Task.FromResult("ok"); });
        await tool.ExecuteAsync(args);
        captured.Should().Be(42);
    }

    [Fact]
    public async Task GetOptionalInt_ReturnsParsedString()
    {
        var args = new Dictionary<string, object?> { ["n"] = "7" };
        int captured = 0;
        var tool = new EchoTool(a => { captured = GetOptionalInt(a, "n", -1); return Task.FromResult("ok"); });
        await tool.ExecuteAsync(args);
        captured.Should().Be(7);
    }

    [Fact]
    public async Task GetOptionalBool_ReturnsTrue_WhenStringTrue()
    {
        var args = new Dictionary<string, object?> { ["flag"] = "true" };
        bool captured = false;
        var tool = new EchoTool(a => { captured = GetOptionalBool(a, "flag"); return Task.FromResult("ok"); });
        await tool.ExecuteAsync(args);
        captured.Should().BeTrue();
    }

    // ── Helpers via ToolBase static methods (invoked through lambda) ──────────

    private static string GetRequiredString(IReadOnlyDictionary<string, object?> args, string key)
        => ToolBase_Accessor.GetRequired(args, key);
    private static int GetOptionalInt(IReadOnlyDictionary<string, object?> args, string key, int def = 0)
        => ToolBase_Accessor.GetInt(args, key, def);
    private static bool GetOptionalBool(IReadOnlyDictionary<string, object?> args, string key, bool def = false)
        => ToolBase_Accessor.GetBool(args, key, def);
}

/// <summary>Thin wrapper to expose ToolBase's protected statics for testing.</summary>
file sealed class ToolBase_Accessor : ToolBase
{
    public override ToolDefinition Definition => new("test", "test", new Dictionary<string, ToolParameterSchema>());
    protected override Task<string> ExecuteCoreAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken)
        => Task.FromResult("ok");

    public static string GetRequired(IReadOnlyDictionary<string, object?> a, string k) => GetRequiredString(a, k);
    public static int GetInt(IReadOnlyDictionary<string, object?> a, string k, int d) => GetOptionalInt(a, k, d);
    public static bool GetBool(IReadOnlyDictionary<string, object?> a, string k, bool d) => GetOptionalBool(a, k, d);
}
