using BotNexus.Tools;

namespace BotNexus.Gateway.Tests.Tools;

/// <summary>
/// Verifies the skill-wrapper not-found diagnostics (issue #2758) are wired into <see cref="ShellTool"/>.
/// A <c>pwsh -File</c> invocation naming a wrapper that does not exist must be rejected BEFORE a
/// process is spawned, with a message that names the skill and lists the closest existing wrapper
/// names - not <c>pwsh</c>'s bare usage banner, which names neither.
/// </summary>
public sealed class ShellToolSkillScriptPreflightTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "bn-2758-" + Guid.NewGuid().ToString("N"));

    private string CreateTeamsSkill()
    {
        var scripts = Path.Combine(_root, "skills", "teams", "scripts");
        Directory.CreateDirectory(scripts);
        foreach (var name in new[] { "ListChatMessages.ps1", "ListChannelMessages.ps1", "GetChatMessage.ps1", "SendMessageToChat.ps1" })
        {
            File.WriteAllText(Path.Combine(scripts, name), "# wrapper");
        }

        return scripts;
    }

    [Fact]
    public async Task ExecuteAsync_MissingSkillWrapper_NamesSkillAndClosestMatches()
    {
        var scripts = CreateTeamsSkill();
        var tool = new ShellTool(shellPreference: ShellPreference.Pwsh);
        var missing = Path.Combine(scripts, "ListMessages.ps1");

        var ex = await Should.ThrowAsync<ArgumentException>(() => tool.ExecuteAsync(
            "skill-preflight",
            new Dictionary<string, object?> { ["command"] = $"pwsh -NoProfile -File '{missing}'" }));

        ex.Message.ShouldContain("teams");
        ex.Message.ShouldContain("ListMessages.ps1");
        ex.Message.ShouldContain("ListChatMessages.ps1");
        ex.Message.ShouldContain("ListChannelMessages.ps1");
        // A near match is reported, never silently executed in place of the request.
        ex.Message.Contains("NOT executed", StringComparison.OrdinalIgnoreCase).ShouldBeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_MissingNonSkillScript_ReportsPlainNotFound()
    {
        Directory.CreateDirectory(_root);
        var tool = new ShellTool(shellPreference: ShellPreference.Pwsh);
        var missing = Path.Combine(_root, "fq.ps1");

        var ex = await Should.ThrowAsync<ArgumentException>(() => tool.ExecuteAsync(
            "generic-preflight",
            new Dictionary<string, object?> { ["command"] = $"pwsh -NoProfile -File '{missing}'" }));

        ex.Message.ShouldContain("fq.ps1");
        // AC5: no bogus candidate list outside a skill directory.
        ex.Message.Contains("Closest matches", StringComparison.Ordinal).ShouldBeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_ExistingSkillWrapper_ExecutesUnchanged()
    {
        var scripts = CreateTeamsSkill();
        var real = Path.Combine(scripts, "Echo.ps1");
        File.WriteAllText(real, "Write-Output 'wrapper-ok'");
        var tool = new ShellTool(shellPreference: ShellPreference.Pwsh);

        var result = await tool.ExecuteAsync(
            "wrapper-ok",
            new Dictionary<string, object?> { ["command"] = $"pwsh -NoProfile -File '{real}'" });

        result.Content[0].Value.ShouldContain("wrapper-ok");
    }

    [Fact]
    public async Task ExecuteAsync_InlineCommand_IsNotTreatedAsAFileTarget()
    {
        var tool = new ShellTool(shellPreference: ShellPreference.Pwsh);

        var result = await tool.ExecuteAsync(
            "inline-ok",
            new Dictionary<string, object?> { ["command"] = "Write-Output 'inline-ok'" });

        result.Content[0].Value.ShouldContain("inline-ok");
    }

    // === Issue #3566: the preflight must not refuse commands whose path is followed by a terminator ===
    //
    // Each of these is a shape that WAS refused in production with "Script not found: <path>;" for a
    // script that exists, because the extractor text-split on "-File" instead of parsing. Together
    // they cover clauses 1-5. They assert the command actually RAN (its output is present), which is
    // the only proof that no pre-execution refusal occurred.

    [Theory]
    [InlineData("pwsh -NoProfile -File '{0}'; Write-Output 'after-semicolon'", "after-semicolon")]   // clause 1
    [InlineData("pwsh -NoProfile -File '{0}' | Select-Object -First 1", "wrapper-ok")]              // clause 2
    [InlineData("pwsh -NoProfile -File '{0}' 2>&1", "wrapper-ok")]                                  // clause 3
    [InlineData("$x = (& pwsh -NoProfile -File '{0}'); Write-Output $x", "wrapper-ok")]             // clause 4
    // Issue #3754 (AC3): the chain operators, previously named in the acceptance criteria but never
    // exercised end-to-end through the tool. `&&` short-circuits on success, so 'after-and' proves
    // the wrapper ran AND the preflight did not refuse the call.
    [InlineData("pwsh -NoProfile -File '{0}' && Write-Output 'after-and'", "after-and")]
    // An unquoted path before a separator - the corpus-dominant shape (671 absorbed ';').
    [InlineData("pwsh -NoProfile -File {0}; Write-Output 'after-bare-semicolon'", "after-bare-semicolon")]
    public async Task ExecuteAsync_ExistingScriptFollowedByATerminator_Executes(string template, string expected)
    {
        var scripts = CreateTeamsSkill();
        var real = Path.Combine(scripts, "Echo.ps1");
        File.WriteAllText(real, "Write-Output 'wrapper-ok'");
        var tool = new ShellTool(shellPreference: ShellPreference.Pwsh);

        var result = await tool.ExecuteAsync(
            "terminator-ok",
            new Dictionary<string, object?>
            {
                ["command"] = string.Format(System.Globalization.CultureInfo.InvariantCulture, template, real)
            });

        result.Content[0].Value.ShouldContain(expected);
    }

    [Fact]
    public async Task ExecuteAsync_FileSwitchOnAnotherCommand_IsNeverAScriptPath()
    {
        // Clause 5. This exact command was refused with
        // "Script not found: <workspace>\|" - the extractor took the PIPE as a filename because
        // -File here is Get-ChildItem's own switch and there is no script path in the command at all.
        var scripts = CreateTeamsSkill();
        var tool = new ShellTool(shellPreference: ShellPreference.Pwsh);

        var result = await tool.ExecuteAsync(
            "gci-file-switch",
            new Dictionary<string, object?>
            {
                ["command"] = $"Get-ChildItem '{scripts}' -Filter '*.ps1' -File | Select-Object -First 1 -ExpandProperty Name"
            });

        result.Content[0].Value.ShouldContain(".ps1");
    }

    [Fact]
    public async Task ExecuteAsync_FileSwitchBeforeAChainOperator_IsNeverAScriptPath()
    {
        // Issue #3754. `-File` as the LAST element of a non-pwsh command, immediately before `&&`,
        // is the worst case for a text-splitting extractor: there is no following token in the same
        // command at all, so the naive reader reaches across the operator into the next command.
        //
        // Non-vacuity: asserting only "no exception" would pass for an extractor that bound nothing
        // for an unrelated reason, so the assertion requires the OUTPUT of both sides of the chain -
        // the directory listing and the right-hand command - proving the whole line executed.
        var scripts = CreateTeamsSkill();
        var tool = new ShellTool(shellPreference: ShellPreference.Pwsh);

        var result = await tool.ExecuteAsync(
            "gci-file-switch-chain",
            new Dictionary<string, object?>
            {
                ["command"] = $"Get-ChildItem '{scripts}' -File | Select-Object -First 1 -ExpandProperty Name && Write-Output 'chain-ran'"
            });

        var output = result.Content[0].Value;
        output.ShouldContain(".ps1");
        output.ShouldContain("chain-ran");
        output.ShouldNotContain("Script not found");
    }

    [Fact]
    public async Task ExecuteAsync_MissingScriptFollowedByASeparator_StillRefusesWithACleanPath()
    {
        // Clause 6. The fix must not weaken the guard: a genuinely absent script is still refused.
        // Non-vacuity: asserting only "it was refused" would also pass for the defective text-split,
        // so the reported path is asserted to carry NO trailing separator or redirect character -
        // the exact corruption ("...ps1;") that made 206 of 316 weekly refusals false positives.
        Directory.CreateDirectory(_root);
        var tool = new ShellTool(shellPreference: ShellPreference.Pwsh);
        var missing = Path.Combine(_root, "fq.ps1");

        var ex = await Should.ThrowAsync<ArgumentException>(() => tool.ExecuteAsync(
            "missing-with-separator",
            new Dictionary<string, object?>
            {
                ["command"] = $"pwsh -NoProfile -File '{missing}'; Write-Output 'tail'"
            }));

        ex.Message.ShouldContain(missing);
        ex.Message.ShouldNotContain(missing + ";");
        ex.Message.ShouldNotContain(missing + "|");
        ex.Message.ShouldNotContain(missing + ")");
        ex.Message.ShouldNotContain(missing + " 2>&1");
        // Issue #3754 (AC3): the chain operators must not survive into the reported path either.
        ex.Message.ShouldNotContain(missing + "&&");
        ex.Message.ShouldNotContain(missing + " &&");
    }

    [Fact]
    public async Task ExecuteAsync_UnparseableCommand_IsAllowedToRunAndReportsItsOwnNativeError()
    {
        // Clause 7. The -File preflight must fail open on a command it cannot parse rather than
        // refuse it on a guess. (The command is invalid, so it fails - but at the SHELL, in the
        // result, not as a pre-execution ArgumentException from this preflight.)
        Directory.CreateDirectory(_root);
        var tool = new ShellTool(shellPreference: ShellPreference.Pwsh);
        var missing = Path.Combine(_root, "nope.ps1");

        var ex = await Record.ExceptionAsync(() => tool.ExecuteAsync(
            "unparseable",
            new Dictionary<string, object?> { ["command"] = $"pwsh -NoProfile -File '{missing}" }));

        // Whatever happens, it must NOT be this preflight's "Script not found" refusal.
        if (ex is ArgumentException argumentException)
        {
            argumentException.Message.ShouldNotContain("Script not found");
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
    }
}
