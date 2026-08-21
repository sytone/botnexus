using System.Text;
using System.Text.RegularExpressions;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// #2653 (raised by @sytone on PR #2630): fence Razor reserved directive keywords that are used as
/// bare dotted/indexed expressions.
///
/// <para>In a <c>.razor</c> file, <c>@section.Name</c> is ambiguous: the Razor parser sees the
/// reserved directive keyword <c>section</c> in an implicit-expression position. Depending on the
/// Razor SDK version this either silently works or fails the build with RZ9979 / RZ2005 / RZ1011.
/// Because the failure is SDK-version dependent it cannot be relied on locally, which is exactly
/// why this fitness function exists rather than trusting the compiler.</para>
///
/// <para><b>The fence targets the ambiguous FORM, never the identifier.</b> A loop variable named
/// <c>section</c> or <c>model</c> is perfectly legal - only the bare <c>@keyword.</c> /
/// <c>@keyword[</c> spelling is banned. The fix is always to wrap the expression explicitly:
/// <c>@(section.Name)</c>.</para>
///
/// <para><b>There is deliberately no allowlist and no baseline.</b> The repository is clean at the
/// time this fence lands; a baseline entry here would mean the predicate is wrong, not that an
/// exemption is warranted.</para>
/// </summary>
public sealed class RazorDirectiveKeywordFenceArchitectureTests : ArchitectureTest
{
    /// <summary>
    /// The Razor reserved directive keywords. This is the set of tokens the Razor parser treats
    /// specially when they appear immediately after <c>@</c> at the start of an implicit
    /// expression. Sourced from the ASP.NET Core Razor directive surface (component + MVC view
    /// directives). Every keyword is handled by the SAME predicate - none is special-cased.
    /// </summary>
    private static readonly string[] s_directiveKeywords =
    [
        "addTagHelper",
        "attribute",
        "code",
        "functions",
        "implements",
        "inherits",
        "inject",
        "layout",
        "model",
        "namespace",
        "page",
        "preservewhitespace",
        "rendermode",
        "removeTagHelper",
        "section",
        "tagHelperPrefix",
        "typeparam",
        "using",
    ];

    /// <summary>
    /// The discriminator is the single character AFTER the keyword.
    /// <list type="bullet">
    ///   <item><c>.</c> or <c>[</c> -&gt; ambiguous expression form (BANNED).</item>
    ///   <item>whitespace, <c>{</c>, <c>"</c>, end-of-line -&gt; genuine directive (ALLOWED).</item>
    /// </list>
    /// The leading <c>(?&lt;![\w@.])</c> guard keeps the fence off escaped <c>@@section</c>, off
    /// email-like text (<c>a@code.x</c>), and off member access on something else
    /// (<c>x.@code.y</c>). The trailing keyword boundary is enforced by the lookahead itself, so
    /// <c>@modelling.Foo</c> (not a keyword) is untouched.
    /// </summary>
    private static readonly Regex s_ambiguousForm = new(
        @"(?<![\w@.])@(" + string.Join("|", s_directiveKeywords) + @")(?=[.\[])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Minimum number of .razor files expected under src/ (AC4 non-vacuity guard).</summary>
    private const int MinimumRazorFileCount = 40;

    private sealed record Offender(string RelativePath, int Line, string Expression, string Text);

    // -------------------------------------------------------------------------------------
    // AC4: structural non-vacuity. A broken glob returning zero files would make the fence
    // pass forever. This repo has hit that exact failure mode before.
    // -------------------------------------------------------------------------------------
    [Fact]
    public void RazorFileDiscovery_IsNotVacuous()
    {
        var files = RazorFiles().ToList();

        files.Count.ShouldBeGreaterThanOrEqualTo(
            MinimumRazorFileCount,
            $"Only {files.Count} .razor file(s) were discovered under {Repository.SourceRoot}. The fence in "
            + $"{nameof(RazorDirectiveKeywordFence_HasNoViolations)} is vacuous unless discovery works. "
            + "If .razor files genuinely moved, update the discovery root - do not lower the floor.");
    }

    // -------------------------------------------------------------------------------------
    // The fence itself.
    // -------------------------------------------------------------------------------------
    [Fact]
    public void RazorDirectiveKeywordFence_HasNoViolations()
    {
        var src = Repository.SourceRoot;
        var offenders = new List<Offender>();

        foreach (var file in RazorFiles())
        {
            var rel = Rel(src, file);
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (var found in FindAmbiguousForms(lines[i]))
                {
                    offenders.Add(new Offender(rel, i + 1, found, lines[i].Trim()));
                }
            }
        }

        if (offenders.Count == 0)
        {
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine(
            $"{offenders.Count} Razor reserved directive keyword(s) are used as bare dotted/indexed "
            + "expressions. Razor parses these in directive position, which fails the build on some "
            + "Razor SDK versions (RZ9979 / RZ2005 / RZ1011) - see issue #2653.");
        sb.AppendLine();
        foreach (var o in offenders.OrderBy(o => o.RelativePath, StringComparer.Ordinal).ThenBy(o => o.Line))
        {
            sb.AppendLine($"  {o.RelativePath}({o.Line}): '@{o.Expression}' -> wrap it as '@({o.Expression}...)'");
            sb.AppendLine($"      {o.Text}");
        }

        sb.AppendLine();
        sb.AppendLine("FIX: wrap the expression in an explicit Razor expression, e.g.");
        sb.AppendLine("       @section.Name   ->   @(section.Name)");
        sb.AppendLine("       @model.Id       ->   @(model.Id)");
        sb.AppendLine("Do NOT rename the variable and do NOT add an allowlist entry - the ambiguity is "
            + "in the '@keyword.' spelling, not in the identifier.");

        throw new Xunit.Sdk.XunitException(sb.ToString());
    }

    // -------------------------------------------------------------------------------------
    // AC3: positive pins. Every genuine directive form must be ALLOWED.
    // -------------------------------------------------------------------------------------
    [Theory]
    [InlineData("@code {")]
    [InlineData("@code")]
    [InlineData("@functions {")]
    [InlineData("@page \"/landing\"")]
    [InlineData("@page \"/cron/{id}\"")]
    [InlineData("@inject IFoo Foo")]
    [InlineData("@using BotNexus.Gateway.Contracts")]
    [InlineData("@using System.Linq")]
    [InlineData("@layout MainLayout")]
    [InlineData("@rendermode InteractiveServer")]
    [InlineData("@namespace BotNexus.Client.Pages")]
    [InlineData("@inherits ComponentBase")]
    [InlineData("@implements IDisposable")]
    [InlineData("@typeparam TItem")]
    [InlineData("@attribute [Authorize]")]
    [InlineData("@model MyViewModel")]
    [InlineData("@preservewhitespace true")]
    [InlineData("@addTagHelper *, Microsoft.AspNetCore.Mvc.TagHelpers")]
    [InlineData("@removeTagHelper *, Microsoft.AspNetCore.Mvc.TagHelpers")]
    [InlineData("@tagHelperPrefix th:")]
    [InlineData("@section Scripts { <script></script> }")]
    [InlineData("@section Scripts")]
    public void GenuineDirectiveForms_AreAllowed(string line)
    {
        FindAmbiguousForms(line).ShouldBeEmpty($"'{line}' is a genuine Razor directive and must not be flagged.");
    }

    /// <summary>
    /// AC1/AC3: disambiguated and unrelated forms must pass. Notably <c>@(section.Name)</c> is the
    /// prescribed fix, so it must never be flagged, and identifiers that merely START with a
    /// keyword must not trip the fence.
    /// </summary>
    [Theory]
    [InlineData("<span>@(section.Name)</span>")]
    [InlineData("<span>@(model.ModelId)</span>")]
    [InlineData("value=\"@(model.ModelId)\" selected=\"@(model.ModelId == _selected)\"")]
    [InlineData("@(using1.Foo)")]
    [InlineData("@modelling.Value")]
    [InlineData("@sections.Count")]
    [InlineData("@codeName.Value")]
    [InlineData("@@section.Name")]
    [InlineData("<a href=\"mailto:someone@domain.com\">x</a>")]
    [InlineData("@_editJob.Model")]
    [InlineData("@foreach (var model in _models)")]
    [InlineData("@if (section.IsCollapsed) { }")]
    [InlineData("@onclick=\"() => Assign(section.SectionId)\"")]
    public void DisambiguatedAndUnrelatedForms_AreAllowed(string line)
    {
        FindAmbiguousForms(line).ShouldBeEmpty($"'{line}' is not the ambiguous form and must not be flagged.");
    }

    // -------------------------------------------------------------------------------------
    // AC6-adjacent: the predicate must actually catch the ambiguous form. If these ever pass
    // empty the fence above is a no-op.
    // -------------------------------------------------------------------------------------
    [Theory]
    [InlineData("<span data-section-id=\"@section.SectionId\">", "section")]
    [InlineData("<option value=\"@model.ModelId\">", "model")]
    [InlineData("@using.Foo", "using")]
    [InlineData("@code.Length", "code")]
    [InlineData("@page.Title", "page")]
    [InlineData("@layout.Name", "layout")]
    [InlineData("@inject.Service", "inject")]
    [InlineData("@section[0]", "section")]
    [InlineData("@model[\"key\"]", "model")]
    [InlineData("@attribute[0]", "attribute")]
    public void AmbiguousForms_AreFlagged(string line, string expectedKeyword)
    {
        FindAmbiguousForms(line).ShouldContain(expectedKeyword, $"'{line}' is the ambiguous form and must be flagged.");
    }

    /// <summary>Every declared keyword is covered uniformly by the one predicate - no special cases.</summary>
    [Fact]
    public void EveryDeclaredKeyword_IsFencedUniformly()
    {
        foreach (var kw in s_directiveKeywords)
        {
            FindAmbiguousForms($"@{kw}.Foo").ShouldContain(kw, $"'@{kw}.Foo' must be flagged.");
            FindAmbiguousForms($"@{kw}[0]").ShouldContain(kw, $"'@{kw}[0]' must be flagged.");
            FindAmbiguousForms($"@{kw} Something").ShouldBeEmpty($"'@{kw} Something' is directive position.");
            FindAmbiguousForms($"@({kw}.Foo)").ShouldBeEmpty($"'@({kw}.Foo)' is already disambiguated.");
        }
    }

    // -------------------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------------------
    private static IReadOnlyList<string> FindAmbiguousForms(string line) =>
        s_ambiguousForm.Matches(line).Select(m => m.Groups[1].Value).ToList();

    private IEnumerable<string> RazorFiles()
    {
        var root = Repository.SourceRoot;
        if (!Directory.Exists(root)) return [];
        return Directory.EnumerateFiles(root, "*.razor", SearchOption.AllDirectories)
            .Where(p =>
                !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.Ordinal);
    }

    private static string Rel(string src, string full)
    {
        var f = Path.GetFullPath(full);
        var r = Path.GetFullPath(src).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return f.StartsWith(r, StringComparison.OrdinalIgnoreCase) ? f[r.Length..] : f;
    }

}
