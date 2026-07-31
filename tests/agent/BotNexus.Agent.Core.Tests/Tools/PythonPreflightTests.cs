using BotNexus.Agent.Core.Tools;

namespace BotNexus.AgentCore.Tests.Tools;

/// <summary>
/// Unit tests for <see cref="PythonPreflight"/> (issue #2417). These assert the observed failure
/// signature - <c>SyntaxError: unterminated string literal</c> from inline <c>python -c</c> scripts -
/// is caught before a process is spawned, and - critically - that legitimate one-liners still pass.
/// </summary>
public class PythonPreflightTests
{
    // === Happy path: valid scripts must NOT be rejected ===

    [Theory]
    [InlineData("print('hi')")]
    [InlineData("print(\"hi\")")]
    [InlineData("import json; print(json.dumps({'a': 1}))")]
    [InlineData("print('it\\'s fine')")]
    [InlineData("print(\"a ' quote\")")]
    [InlineData("print('a \" quote')")]
    [InlineData("print('''multi\nline''')")]
    [InlineData("print(\"\"\"multi\nline\"\"\")")]
    [InlineData("x = [1, 2, 3]\nprint(x[0])")]
    [InlineData("print('# not a comment')")]
    [InlineData("# just a comment")]
    [InlineData("print(r'C:\\path\\to')")]
    [InlineData("print(f'{1 + 1}')")]
    [InlineData("print('(unbalanced in string')")]
    public void Validate_ValidScript_ReturnsNull(string script)
    {
        PythonPreflight.Validate(script).ShouldBeNull();
    }

    [Fact]
    public void ThrowIfInvalid_ValidScript_DoesNotThrow()
    {
        Should.NotThrow(() => PythonPreflight.ThrowIfInvalid("print('hi')"));
    }

    // === Sad path: unterminated string literal (the observed signature) ===

    [Theory]
    [InlineData("print('hi)")]
    [InlineData("print(\"hi)")]
    [InlineData("x = 'abc\nprint(x)")]
    public void Validate_UnterminatedStringLiteral_IsRejected(string script)
    {
        var error = PythonPreflight.Validate(script);
        error.ShouldNotBeNull();
        error!.Message.ShouldBe("SyntaxError: unterminated string literal");
    }

    [Fact]
    public void Validate_UnterminatedTripleQuotedString_IsRejected()
    {
        var error = PythonPreflight.Validate("x = '''abc\nprint(x)");
        error.ShouldNotBeNull();
        error!.Message.ShouldBe("SyntaxError: unterminated triple-quoted string literal");
    }

    // === Sad path: unbalanced brackets ===

    [Theory]
    [InlineData("print('hi'", "'(' was never closed")]
    [InlineData("x = [1, 2", "'[' was never closed")]
    [InlineData("x = {'a': 1", "'{' was never closed")]
    public void Validate_UnclosedBracket_IsRejected(string script, string expectedFragment)
    {
        var error = PythonPreflight.Validate(script);
        error.ShouldNotBeNull();
        error!.Message.ShouldBe($"SyntaxError: {expectedFragment}");
    }

    [Theory]
    [InlineData("print('hi'))")]
    [InlineData("x = 1]")]
    public void Validate_UnmatchedClosingBracket_IsRejected(string script)
    {
        var error = PythonPreflight.Validate(script);
        error.ShouldNotBeNull();
        error!.Message.ShouldStartWith("SyntaxError: unmatched");
    }

    // === Rejection message content ===

    [Fact]
    public void ThrowIfInvalid_Rejected_ThrowsWithRemediationHint()
    {
        var ex = Should.Throw<ArgumentException>(() => PythonPreflight.ThrowIfInvalid("print('hi)"));

        ex.Message.ShouldContain("SyntaxError: unterminated string literal");
        ex.Message.ShouldContain("tmp/");
        ex.Message.ShouldContain(".py");
        ex.Message.ShouldContain("offset");
    }

    // === Executable / inline-script detection ===

    [Theory]
    [InlineData("python", true)]
    [InlineData("python3", true)]
    [InlineData("py", true)]
    [InlineData("python.exe", true)]
    [InlineData("PYTHON3", true)]
    [InlineData("python3.12", true)]
    [InlineData(@"C:\Python312\python.exe", true)]
    [InlineData("/usr/bin/python3", true)]
    [InlineData("pwsh", false)]
    [InlineData("bash", false)]
    [InlineData("pythonic", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsPythonExecutable_ClassifiesCorrectly(string? exe, bool expected)
    {
        PythonPreflight.IsPythonExecutable(exe).ShouldBe(expected);
    }

    [Fact]
    public void TryGetInlineScript_ShellToolStyle_ReturnsTrailingScript()
    {
        var baseArgs = new[] { "-X", "utf8", "-c" };
        var found = PythonPreflight.TryGetInlineScript(baseArgs, "print('hi)", out var script);

        found.ShouldBeTrue();
        script.ShouldBe("print('hi)");
    }

    [Fact]
    public void TryGetInlineScript_ExecToolStyle_ReturnsNextArgAfterCFlag()
    {
        var args = new[] { "-X", "utf8", "-c", "print('hi)" };
        var found = PythonPreflight.TryGetInlineScript(args, inlineScript: null, out var script);

        found.ShouldBeTrue();
        script.ShouldBe("print('hi)");
    }

    [Fact]
    public void TryGetInlineScript_FileInvocation_IsNotPreflighted()
    {
        // A script *path* is not inline code - must be skipped entirely.
        var args = new[] { "-X", "utf8", "tmp/q.py" };
        PythonPreflight.TryGetInlineScript(args, inlineScript: null, out _).ShouldBeFalse();
    }

    [Fact]
    public void TryGetInlineScript_ModuleInvocation_IsNotPreflighted()
    {
        var args = new[] { "-m", "pip", "install", "x" };
        PythonPreflight.TryGetInlineScript(args, inlineScript: null, out _).ShouldBeFalse();
    }
}
