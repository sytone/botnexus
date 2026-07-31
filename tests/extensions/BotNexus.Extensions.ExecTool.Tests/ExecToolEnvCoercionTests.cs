using System.Text.Json;
using BotNexus.Agent.Core.Types;
using System.IO.Abstractions.TestingHelpers;

namespace BotNexus.Extensions.ExecTool.Tests;

/// <summary>
/// Regression coverage for issue #2415, row <c>exec.env</c>. The validator accepted only
/// <c>IReadOnlyDictionary&lt;string,string&gt;</c> or a <see cref="JsonElement"/> object, but the tool
/// pipeline commonly delivers a deserialized <c>Dictionary&lt;string,object?&gt;</c>. The verbatim
/// payload from the issue - an object whose values are strings - was therefore rejected by a message
/// stating the exact requirement the payload already satisfied.
/// </summary>
[Collection(ExecToolBackgroundRegistryCollection.Name)]
public class ExecToolEnvCoercionTests : IDisposable
{
    private readonly ExecTool _tool = new(workingDirectory: null, fileSystem: new MockFileSystem());

    public void Dispose() => ExecTool.ClearBackgroundProcesses();

    /// <summary>
    /// The verbatim <c>env</c> payload from #2415, in the object-valued dictionary shape the
    /// pipeline delivers.
    /// </summary>
    [Fact]
    public async Task PrepareArguments_AcceptsObjectValuedEnvDictionary()
    {
        var args = new Dictionary<string, object?>
        {
            ["command"] = new List<string> { "echo", "hi" },
            ["env"] = new Dictionary<string, object?> { ["PYTHONUTF8"] = "1" },
        };

        var prepared = await _tool.PrepareArgumentsAsync(args);

        prepared.ShouldNotBeNull();
    }

    /// <summary>
    /// The full call shape from #2415. The issue explicitly asks whether the sibling numeric
    /// <c>timeoutMs</c> property influences the <c>env</c> branch; it must not.
    /// </summary>
    [Fact]
    public async Task PrepareArguments_AcceptsIssuePayloadWithSiblingNumericProperty()
    {
        var args = new Dictionary<string, object?>
        {
            ["command"] = new List<string> { "python", "-X", "utf8", "C:/repos/scripts/Run-All.py", "--publish" },
            ["timeoutMs"] = 1200000,
            ["env"] = new Dictionary<string, object?> { ["PYTHONUTF8"] = "1" },
        };

        var prepared = await _tool.PrepareArgumentsAsync(args);

        prepared.ShouldNotBeNull();
    }

    /// <summary>
    /// The same payload as JSON, exactly as a provider would deliver it after streaming parse.
    /// </summary>
    [Fact]
    public async Task PrepareArguments_AcceptsIssuePayloadWithJsonElementValues()
    {
        var env = JsonDocument.Parse("""{"PYTHONUTF8":"1"}""").RootElement.Clone();
        var args = new Dictionary<string, object?>
        {
            ["command"] = new List<string> { "python", "-X", "utf8", "C:/repos/scripts/Run-All.py", "--publish" },
            ["timeoutMs"] = 1200000,
            ["env"] = env.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value),
        };

        var prepared = await _tool.PrepareArgumentsAsync(args);

        prepared.ShouldNotBeNull();
    }

    /// <summary>
    /// A scalar JSON value that is not a string still has an unambiguous string form and is
    /// accepted - environment variables are strings by definition.
    /// </summary>
    [Fact]
    public async Task PrepareArguments_AcceptsScalarNonStringEnvValues()
    {
        var args = new Dictionary<string, object?>
        {
            ["command"] = new List<string> { "echo", "hi" },
            ["env"] = new Dictionary<string, object?>
            {
                ["MY_COUNT"] = 3,
                ["MY_FLAG"] = true,
            },
        };

        var prepared = await _tool.PrepareArgumentsAsync(args);

        prepared.ShouldNotBeNull();
    }

    /// <summary>
    /// Widening the accepted shapes must NOT degrade into "anything goes": a nested object or array
    /// has no meaningful environment-variable string form and must still be rejected - and the
    /// message must name the offending KEY so the model can fix the one entry at fault.
    /// </summary>
    [Fact]
    public async Task PrepareArguments_RejectsNestedObjectEnvValueNamingTheKey()
    {
        var args = new Dictionary<string, object?>
        {
            ["command"] = new List<string> { "echo", "hi" },
            ["env"] = new Dictionary<string, object?>
            {
                ["GOOD_VAR"] = "1",
                ["BAD_VAR"] = new Dictionary<string, object?> { ["nested"] = "x" },
            },
        };

        var act = () => _tool.PrepareArgumentsAsync(args);
        var ex = await act.ShouldThrowAsync<ArgumentException>();

        ex.Message.ShouldContain("BAD_VAR");
        ex.Message.ShouldContain("env");
    }

    [Fact]
    public async Task PrepareArguments_RejectsArrayEnvValueNamingTheKey()
    {
        var args = new Dictionary<string, object?>
        {
            ["command"] = new List<string> { "echo", "hi" },
            ["env"] = new Dictionary<string, object?>
            {
                ["LIST_VAR"] = JsonDocument.Parse("""["a","b"]""").RootElement.Clone(),
            },
        };

        var act = () => _tool.PrepareArgumentsAsync(args);
        var ex = await act.ShouldThrowAsync<ArgumentException>();

        ex.Message.ShouldContain("LIST_VAR");
    }

    /// <summary>
    /// A completely wrong shape for <c>env</c> (not a mapping at all) must state what was received
    /// and what was expected - the #2415 complaint was messages that assert without diagnosing.
    /// </summary>
    [Fact]
    public async Task PrepareArguments_RejectionMessageStatesReceivedAndExpectedShape()
    {
        var args = new Dictionary<string, object?>
        {
            ["command"] = new List<string> { "echo", "hi" },
            ["env"] = "PYTHONUTF8=1",
        };

        var act = () => _tool.PrepareArgumentsAsync(args);
        var ex = await act.ShouldThrowAsync<ArgumentException>();

        ex.Message.ShouldContain("env");
        ex.Message.ShouldContain("received", Case.Insensitive);
        ex.Message.ShouldContain("expected", Case.Insensitive);
    }

    /// <summary>
    /// Widened coercion must not create a bypass of the security blocklist: a blocked key delivered
    /// in the newly-accepted object-valued shape must still be rejected.
    /// </summary>
    [Fact]
    public async Task PrepareArguments_StillRejectsBlockedKeyInObjectValuedDictionary()
    {
        var args = new Dictionary<string, object?>
        {
            ["command"] = new List<string> { "echo", "hi" },
            ["env"] = new Dictionary<string, object?> { ["LD_PRELOAD"] = "/tmp/evil.so" },
        };

        var act = () => _tool.PrepareArgumentsAsync(args);
        var ex = await act.ShouldThrowAsync<ArgumentException>();

        ex.Message.ShouldContain("LD_");
    }
}
