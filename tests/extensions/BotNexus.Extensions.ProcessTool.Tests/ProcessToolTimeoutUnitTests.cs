using System.Text.Json;
using BotNexus.Agent.Core.Tools;

namespace BotNexus.Extensions.ProcessTool.Tests;

/// <summary>
/// Pins the millisecond semantics of the process tool's wait argument (issue #2955).
/// </summary>
public sealed class ProcessToolTimeoutUnitTests
{
    /// <summary>
    /// Clause 1: the canonical argument is <c>timeoutMs</c> and its description names milliseconds.
    /// </summary>
    [Fact]
    public void Schema_ExposesTimeoutMs_WithMillisecondDescription()
    {
        var props = GetProperties();

        props.TryGetProperty("timeoutMs", out var timeoutMs).ShouldBeTrue();
        timeoutMs.GetProperty("description").GetString()!
            .Contains("millisecond", StringComparison.OrdinalIgnoreCase).ShouldBeTrue(
                "the canonical argument must state its unit in its own description");
    }

    /// <summary>
    /// Clause 1: <c>timeout</c> is still accepted, marked deprecated, and still means milliseconds.
    /// </summary>
    [Fact]
    public void Schema_RetainsTimeout_AsDeprecatedMillisecondAlias()
    {
        var props = GetProperties();

        props.TryGetProperty("timeout", out var timeout).ShouldBeTrue(
            "the legacy spelling must remain accepted for one release");

        var description = timeout.GetProperty("description").GetString()!;
        description.Contains("Deprecated", StringComparison.OrdinalIgnoreCase).ShouldBeTrue();
        description.Contains("millisecond", StringComparison.OrdinalIgnoreCase).ShouldBeTrue();
    }

    /// <summary>
    /// Clause 3: the tool declares its unit rather than leaving the executor to infer one.
    /// </summary>
    [Fact]
    public void DeclaresMillisecondTimeoutArgument_WithLegacyAlias()
    {
        var tool = CreateTool();

        var declaration = tool.TimeoutArgument.ShouldNotBeNull();
        declaration.ArgumentName.ShouldBe("timeoutMs");
        declaration.Unit.ShouldBe(ToolTimeoutUnit.Milliseconds);
        declaration.DeprecatedAliasName.ShouldBe("timeout");
    }

    /// <summary>
    /// A process tool call asking for 5000 must resolve to five seconds, never five thousand.
    /// </summary>
    [Fact]
    public void Timeout5000_ResolvesToFiveSeconds()
    {
        var declaration = CreateTool().TimeoutArgument.ShouldNotBeNull();

        declaration.ToTimeSpan(5000).ShouldBe(TimeSpan.FromSeconds(5));
        declaration.ToTimeSpan(5000).ShouldNotBe(TimeSpan.FromSeconds(5000));
    }

    private static ProcessTool CreateTool() => new(new ProcessManager());

    private static JsonElement GetProperties() =>
        CreateTool().Definition.Parameters.GetProperty("properties");
}
