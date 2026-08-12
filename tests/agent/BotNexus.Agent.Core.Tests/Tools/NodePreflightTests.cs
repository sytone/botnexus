using BotNexus.Agent.Core.Tools;

namespace BotNexus.AgentCore.Tests.Tools;

/// <summary>
/// Unit tests for <see cref="NodePreflight"/> (issue #2762, AC4 - the <c>node -e</c> half of the
/// inline-source preflight whose <c>python -c</c> half shipped in <c>ad4a9491</c>). These assert the
/// observed failure signature - an unterminated string literal in an inline <c>node -e</c> one-liner -
/// is caught before a process is spawned, and - critically - that legitimate one-liners still pass.
/// </summary>
public class NodePreflightTests
{
    // === Happy path: valid scripts must NOT be rejected ===

    [Theory]
    [InlineData("console.log('hi')")]
    [InlineData("console.log(\"hi\")")]
    [InlineData("console.log(`hi`)")]
    [InlineData("console.log(JSON.stringify({a: 1}))")]
    [InlineData("console.log('it\\'s fine')")]
    [InlineData("console.log(\"a ' quote\")")]
    [InlineData("console.log('a \" quote')")]
    [InlineData("const x = [1, 2, 3]; console.log(x[0])")]
    [InlineData("// just a comment")]
    [InlineData("console.log('// not a comment')")]
    [InlineData("/* block */ console.log(1)")]
    [InlineData("console.log('(unbalanced in string')")]
    [InlineData("console.log(`multi\nline`)")]
    [InlineData("const n = 10 / 2; console.log(n)")]
    [InlineData("console.log(`${1 + 1}`)")]
    [InlineData("console.log('x'.replace(/['\"]/g, ''))")]
    public void Validate_ValidScript_ReturnsNull(string script)
    {
        NodePreflight.Validate(script).ShouldBeNull();
    }

    [Fact]
    public void ThrowIfInvalid_ValidScript_DoesNotThrow()
    {
        Should.NotThrow(() => NodePreflight.ThrowIfInvalid("console.log('hi')"));
    }

    // === Sad path: unterminated string literal (the observed signature) ===

    [Theory]
    [InlineData("console.log('hi)")]
    [InlineData("console.log(\"hi)")]
    [InlineData("const x = 'abc\nconsole.log(x)")]
    public void Validate_UnterminatedStringLiteral_IsRejected(string script)
    {
        var error = NodePreflight.Validate(script);
        error.ShouldNotBeNull();
        error!.Message.ShouldBe("SyntaxError: Invalid or unexpected token (unterminated string literal)");
    }

    [Fact]
    public void Validate_UnterminatedTemplateLiteral_IsRejected()
    {
        var error = NodePreflight.Validate("const x = `abc\nconsole.log(x)");
        error.ShouldNotBeNull();
        error!.Message.ShouldBe("SyntaxError: Unterminated template literal");
    }

    // === Sad path: unbalanced brackets ===

    [Theory]
    [InlineData("console.log('hi'", "'(' was never closed")]
    [InlineData("const x = [1, 2", "'[' was never closed")]
    [InlineData("const x = {a: 1", "'{' was never closed")]
    public void Validate_UnclosedBracket_IsRejected(string script, string expectedFragment)
    {
        var error = NodePreflight.Validate(script);
        error.ShouldNotBeNull();
        error!.Message.ShouldBe($"SyntaxError: Unexpected end of input ({expectedFragment})");
    }

    [Theory]
    [InlineData("console.log('hi'))")]
    [InlineData("const x = 1]")]
    public void Validate_UnmatchedClosingBracket_IsRejected(string script)
    {
        var error = NodePreflight.Validate(script);
        error.ShouldNotBeNull();
        error!.Message.ShouldStartWith("SyntaxError: Unexpected token");
    }

    // === Rejection message content ===

    [Fact]
    public void ThrowIfInvalid_Rejected_ThrowsWithRemediationHint()
    {
        var ex = Should.Throw<ArgumentException>(() => NodePreflight.ThrowIfInvalid("console.log('hi)"));

        ex.Message.ShouldContain("unterminated string literal");
        ex.Message.ShouldContain("tmp/");
        ex.Message.ShouldContain(".js");
        ex.Message.ShouldContain("offset");
    }

    // === Executable / inline-script detection ===

    [Theory]
    [InlineData("node", true)]
    [InlineData("nodejs", true)]
    [InlineData("node.exe", true)]
    [InlineData("NODE", true)]
    [InlineData(@"C:\Program Files\nodejs\node.exe", true)]
    [InlineData("/usr/bin/node", true)]
    [InlineData("nodemon", false)]
    [InlineData("npx", false)]
    [InlineData("python", false)]
    [InlineData("pwsh", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsNodeExecutable_ClassifiesCorrectly(string? exe, bool expected)
    {
        NodePreflight.IsNodeExecutable(exe).ShouldBe(expected);
    }

    [Fact]
    public void TryGetInlineScript_ShellToolStyle_ReturnsTrailingScript()
    {
        var baseArgs = new[] { "-e" };
        var found = NodePreflight.TryGetInlineScript(baseArgs, "console.log('hi)", out var script);

        found.ShouldBeTrue();
        script.ShouldBe("console.log('hi)");
    }

    [Fact]
    public void TryGetInlineScript_ExecToolStyle_ReturnsNextArgAfterEvalFlag()
    {
        var args = new[] { "--eval", "console.log('hi)" };
        var found = NodePreflight.TryGetInlineScript(args, inlineScript: null, out var script);

        found.ShouldBeTrue();
        script.ShouldBe("console.log('hi)");
    }

    [Fact]
    public void TryGetInlineScript_AttachedEvalValue_IsExtracted()
    {
        var args = new[] { "--eval=console.log('hi)" };
        var found = NodePreflight.TryGetInlineScript(args, inlineScript: null, out var script);

        found.ShouldBeTrue();
        script.ShouldBe("console.log('hi)");
    }

    [Fact]
    public void TryGetInlineScript_FileInvocation_IsNotPreflighted()
    {
        // A script *path* is not inline code - must be skipped entirely.
        var args = new[] { "tmp/q.js" };
        NodePreflight.TryGetInlineScript(args, inlineScript: null, out _).ShouldBeFalse();
    }

    // === Negative: a command merely mentioning -e without invoking node is unaffected ===

    [Fact]
    public void NonNodeCommandMentioningDashE_IsNotPreflighted()
    {
        // `grep -e "pattern` has an unterminated quote but is not a node invocation: the
        // executable gate must keep the preflight off it entirely.
        NodePreflight.IsNodeExecutable("grep").ShouldBeFalse();
        NodePreflight.IsNodeExecutable("sed").ShouldBeFalse();

        // And the arg-shape gate alone must not claim a non-node -e payload.
        NodePreflight.IsNodeExecutable("bash").ShouldBeFalse();
    }
}
