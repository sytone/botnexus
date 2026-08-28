using System.Diagnostics;
using System.Text;
using BotNexus.Agent.Core.Tools;

namespace BotNexus.AgentCore.Tests.Tools;

/// <summary>
/// Regression suite for issue #2757 - the <c>Nested quoting detected</c> heuristic rejected
/// commands that the real PowerShell parser accepts. Forensics over a 7-day corpus found 20 of 20
/// distinct rejections carrying that reason parsed with ZERO errors, i.e. the rule was a pure
/// false-positive generator, while the sibling parser-backed rules (unterminated string, empty pipe
/// element, missing closing brace) were catching real defects and must keep refusing.
/// </summary>
/// <remarks>
/// The central assertion (<see cref="Validate_Corpus_RefusesOnlyWhenRealParserReportsAnError"/>)
/// derives its expectation from <c>[System.Management.Automation.Language.Parser]::ParseInput</c>
/// at test time rather than from a hand-maintained verdict list, because a hand-maintained list is
/// precisely the kind of drift this issue removes.
/// </remarks>
public class PowerShellPreflightNestedQuotingRegressionTests
{
    /// <summary>
    /// Commands that parse cleanly and therefore must NOT be refused. Shapes drawn from the
    /// #2757 corpus of real rejections.
    /// </summary>
    public static readonly string[] ParseCleanCorpus =
    {
        "pwsh -NoProfile -File scripts/x.ps1 -Json '{\"a\":\"b\",\"c\":\"$x\"}'",
        "pwsh -NoProfile -File scripts/SendMessageToChat.ps1 -Json '{\"name\":\"value\"}'",
        "gh api repos/o/r/issues -f title='has \"quotes\" and $vars'",
        "jq --jq '.items[] | select(.name==\"x\")' file.json",
        "pwsh -NoProfile -Command 'Write-Output \"hi\"'",
        "Get-Content x.json | ConvertFrom-Json",
        "Write-Output '$notAVariable'",
        "az rest --uri https://x --body '{\"a\":1}'",
        "Get-Process | Sort-Object CPU | Select-Object -First 5",
        "Get-ChildItem | Where-Object { $_.Length -gt 0 }",
        "$a = @{ Name = \"x\" }; $a",
        "if ($true) { Write-Output 'hi' }",
    };

    /// <summary>Commands with genuine parser errors that must keep being refused.</summary>
    public static readonly string[] ParseBrokenCorpus =
    {
        "Get-Item 'unterminated",
        "Get-Process | | Sort-Object",
        "if ($true) { Write-Output 'hi'",
        "Get-Process |",
    };

    // === Clause 1 / clause 4: the documented -Json form must not be refused. ===
    // Re-enabling the unconditional "Nested quoting detected" rule reddens THIS test by name.
    [Fact]
    public void Validate_SingleQuotedJsonPayload_IsNotRefused()
    {
        const string Script = "pwsh -NoProfile -File scripts/x.ps1 -Json '{\"a\":\"b\",\"c\":\"$x\"}'";
        PowerShellPreflight.Validate(Script).ShouldBeNull();
    }

    // === Clause 5: the teams/generated-skill documented calling convention, end to end. ===
    [Fact]
    public void ThrowIfInvalid_GeneratedSkillJsonCallingConvention_DoesNotThrow()
    {
        const string Script =
            "pwsh -NoProfile -File scripts/SendMessageToChat.ps1 -Json '{\"name\":\"value\"}'";

        var args = new[] { "-NoProfile", "-Command", Script };
        PowerShellPreflight.TryGetInlineScript(args, inlineScript: null, out var extracted).ShouldBeTrue();
        extracted.ShouldBe(Script);
        Should.NotThrow(() => PowerShellPreflight.ThrowIfInvalid(extracted));
    }

    [Fact]
    public void Validate_SingleQuotedValueContainingDollarAndQuote_IsInertAndAllowed()
    {
        // Inside a single-quoted PowerShell string both '"' and '$' are literal by language
        // definition, so no outer layer can consume them. This is the premise the old heuristic got
        // wrong.
        //
        // Issue #3576: this fixture used to be the bare fragment `--jq '...'`, which is NOT a valid
        // command in isolation - real pwsh rejects it with "Missing expression after unary operator
        // '--'" (verified by executing it). The old hand-rolled scanner had no concept of expression
        // position, so it returned null and the fixture silently encoded that blind spot as though it
        // were the language rule. The premise under test is about the QUOTING of the value, so the
        // fixture now carries that value on a real command line where the argument actually is an
        // argument. Verified against Parser.ParseInput: zero errors. The assertion is unchanged and
        // still fails if a heuristic starts refusing single-quoted values.
        PowerShellPreflight
            .Validate("gh pr view 1 --json body --jq '.a | select(.b==\"$c\")'")
            .ShouldBeNull();

        // Same premise on a different tool, so the rule stays general rather than one-command-shaped.
        PowerShellPreflight.Validate("jq '.a | select(.b==\"$c\")' file.json").ShouldBeNull();
    }

    // === Clause 3: the parser-backed rules must still refuse, each pinned by name. ===
    [Fact]
    public void Validate_UnterminatedSingleQuotedString_IsStillRefused()
    {
        var error = PowerShellPreflight.Validate("Get-Item 'unterminated");
        error.ShouldNotBeNull();
        error!.Message.ShouldContain("missing the terminator");
    }

    [Fact]
    public void Validate_EmptyPipeElement_IsStillRefused()
    {
        var error = PowerShellPreflight.Validate("Get-Process | | Sort-Object");
        error.ShouldNotBeNull();
        error!.Message.ShouldContain("empty pipe element");
    }

    [Fact]
    public void Validate_MissingClosingBrace_IsStillRefused()
    {
        var error = PowerShellPreflight.Validate("if ($true) { Write-Output 'hi'");
        error.ShouldNotBeNull();
        error!.Message.ShouldContain("Missing closing '}'");
    }

    // === Clause 2: corpus replay, expectation derived from the real parser. ===
    [Theory]
    [MemberData(nameof(FullCorpus))]
    public void Validate_Corpus_RefusesOnlyWhenRealParserReportsAnError(string script)
    {
        var parserErrors = PowerShellParserProbe.CountParseErrors(script);
        if (parserErrors is null)
        {
            // pwsh is unavailable on this host; the deterministic clause-1/3 facts above still run.
            return;
        }

        var refused = PowerShellPreflight.Validate(script) is not null;
        if (refused)
        {
            parserErrors.Value.ShouldBeGreaterThan(
                0,
                $"Preflight refused a command the real PowerShell parser accepts: {script}");
        }
    }

    public static TheoryData<string> FullCorpus()
    {
        var data = new TheoryData<string>();
        foreach (var script in ParseCleanCorpus)
        {
            data.Add(script);
        }

        foreach (var script in ParseBrokenCorpus)
        {
            data.Add(script);
        }

        return data;
    }
}

/// <summary>
/// Runs <c>[System.Management.Automation.Language.Parser]::ParseInput</c> out of process via
/// <c>pwsh</c>. The solution deliberately does not reference <c>Microsoft.PowerShell.SDK</c>
/// (see <see cref="PowerShellPreflight"/> remarks), so the authoritative parser is reached through
/// the shell instead of by taking a multi-tens-of-megabyte managed dependency on the product side.
/// </summary>
internal static class PowerShellParserProbe
{
    private static readonly Lazy<string?> Executable = new(FindPwsh);

    /// <summary>
    /// Returns the number of parser errors for <paramref name="script"/>, or <see langword="null"/>
    /// when no PowerShell host is available to ask.
    /// </summary>
    public static int? CountParseErrors(string script)
    {
        var exe = Executable.Value;
        if (exe is null)
        {
            return null;
        }

        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(script));
        var probe =
            "$s = [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + encoded + "'));"
            + "$e = $null; $t = $null;"
            + "[void][System.Management.Automation.Language.Parser]::ParseInput($s, [ref]$t, [ref]$e);"
            + "Write-Output ($e.Count)";

        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(probe);

        using var process = Process.Start(psi);
        if (process is null)
        {
            return null;
        }

        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit(60_000);
        return int.TryParse(stdout.Trim(), out var count) ? count : null;
    }

    private static string? FindPwsh()
    {
        foreach (var candidate in new[] { "pwsh", "powershell" })
        {
            try
            {
                var psi = new ProcessStartInfo(candidate)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-NonInteractive");
                psi.ArgumentList.Add("-Command");
                psi.ArgumentList.Add("Write-Output ok");

                using var process = Process.Start(psi);
                if (process is null)
                {
                    continue;
                }

                _ = process.StandardOutput.ReadToEnd();
                process.WaitForExit(60_000);
                if (process.ExitCode == 0)
                {
                    return candidate;
                }
            }
            catch (Exception)
            {
                // Not on PATH for this host - fall through to the next candidate.
            }
        }

        return null;
    }
}
