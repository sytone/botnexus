using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fence for issue #2955 AC3/AC4 - a tool's timeout argument must carry its unit, and
/// the executor must never infer a unit from an argument's name.
///
/// <para><b>Why a structural fence and not just behaviour tests.</b> The behaviour tests prove the
/// executor converts correctly for the tools that exist today. They cannot prove a NEW tool will
/// not reintroduce the collision: the defect was a convention gap across an open tool surface, not
/// a bug inside any single tool. A future extension shipping <c>"timeout"</c> with a millisecond
/// description would keep every behaviour test green while recreating a 1000x budget inflation the
/// moment the executor is asked to widen for it.</para>
///
/// <para><b>Rule 1</b> - no tool schema declares an argument named exactly <c>timeout</c> whose
/// description states milliseconds, unless it is explicitly marked as the deprecated alias.</para>
/// <para><b>Rule 2</b> - <c>ToolExecutor</c> contains no name-based unit inference: it must not
/// read a literal <c>"timeout"</c> / <c>"timeoutMs"</c> argument key directly.</para>
/// <para><b>Rule 3</b> - non-vacuity: the scan actually finds tool sources and at least one real
/// <see cref="ToolTimeoutArgumentDeclaration"/> declaration, so the fence cannot pass by scanning
/// an empty candidate set.</para>
/// </summary>
public sealed class ToolTimeoutUnitDeclarationArchitectureTests : ArchitectureTest
{
    private const string ToolTimeoutArgumentDeclaration = "TimeoutArgument";


    /// <summary>
    /// AC4 - enumerate every tool schema and fail if a bare <c>timeout</c> argument claims
    /// milliseconds without being marked deprecated.
    /// </summary>
    [Fact]
    public void No_tool_exposes_a_bare_timeout_argument_documented_in_milliseconds()
    {
        var offenders = new List<string>();
        var scanned = 0;

        foreach (var file in EnumerateToolSources())
        {
            var text = File.ReadAllText(file);
            scanned++;

            // Match a JSON schema property named exactly "timeout" and capture its description.
            foreach (Match match in Regex.Matches(
                text,
                "\"timeout\"\\s*:\\s*\\{[^}]*?\"description\"\\s*:\\s*\"(?<desc>[^\"]*)\"",
                RegexOptions.Singleline))
            {
                var description = match.Groups["desc"].Value;

                var claimsMilliseconds =
                    description.Contains("millisecond", StringComparison.OrdinalIgnoreCase)
                    || Regex.IsMatch(description, @"\bN?\s?ms\b", RegexOptions.IgnoreCase);

                var markedDeprecated =
                    description.Contains("deprecated", StringComparison.OrdinalIgnoreCase);

                if (claimsMilliseconds && !markedDeprecated)
                {
                    offenders.Add($"{Path.GetFileName(file)}: \"{description}\"");
                }
            }
        }

        scanned.ShouldBeGreaterThan(0, "the fence must actually scan tool sources");

        offenders.ShouldBeEmpty(
            "an argument named 'timeout' must mean seconds everywhere. A millisecond argument must "
            + "be named 'timeoutMs' (a deprecated 'timeout' alias is permitted). Offenders: "
            + string.Join(" | ", offenders));
    }

    /// <summary>
    /// AC3 - the executor must read the unit from the tool's declaration, not from a hardcoded
    /// argument name.
    /// </summary>
    [Fact]
    public void ToolExecutor_does_not_infer_a_unit_from_an_argument_name()
    {
        var executor = Path.Combine(
            Repository.Root, "src", "agent", "BotNexus.Agent.Core", "Loop", "ToolExecutor.cs");

        File.Exists(executor).ShouldBeTrue($"expected ToolExecutor at {executor}");

        var code = StripComments(File.ReadAllText(executor));

        Regex.IsMatch(code, "TryGetValue\\(\\s*\"timeout(Ms)?\"").ShouldBeFalse(
            "ToolExecutor must not read a literal timeout argument name - the unit is declared by "
            + "the tool via TimeoutArgument. Name-based inference is exactly the #2955 defect.");

        code.Contains(ToolTimeoutArgumentDeclaration, StringComparison.Ordinal).ShouldBeTrue(
            "ToolExecutor must resolve the requested timeout through the tool's declaration");
    }

    /// <summary>
    /// Non-vacuity - at least one real tool declares its timeout unit, so Rule 2's declaration
    /// requirement is backed by actual producers.
    /// </summary>
    [Fact]
    public void At_least_one_tool_declares_its_timeout_unit()
    {
        var declaring = EnumerateToolSources()
            .Where(f => Regex.IsMatch(
                File.ReadAllText(f),
                @"ToolTimeoutArgument\?\s+TimeoutArgument\s*=>"))
            .Select(Path.GetFileName)
            .ToList();

        declaring.Count.ShouldBeGreaterThanOrEqualTo(
            3,
            "the shell, exec and process tools all expose caller-requested timeouts and must each "
            + "declare their unit. Found: " + string.Join(", ", declaring));
    }

    private IEnumerable<string> EnumerateToolSources()
    {
        var roots = new[]
        {
            Path.Combine(Repository.Root, "src", "gateway"),
            Path.Combine(Repository.Root, "src", "extensions"),
            Path.Combine(Repository.Root, "src", "agent")
        };

        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(root, "*Tool*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    continue;
                }

                yield return file;
            }
        }
    }

    /// <summary>
    /// Strips comments so the executor scan cannot fire on its own explanatory prose about the
    /// argument names it deliberately no longer reads.
    /// </summary>
    private static string StripComments(string code)
    {
        code = Regex.Replace(code, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        code = Regex.Replace(code, @"^\s*//.*$", string.Empty, RegexOptions.Multiline);
        return code;
    }

}
