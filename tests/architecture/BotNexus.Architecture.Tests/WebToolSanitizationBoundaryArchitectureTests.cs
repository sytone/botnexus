using System.Text.RegularExpressions;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness function for issue #2813 AC3: the untrusted-content filter applied to web
/// tool output must be written in exactly ONE place per tool, and never re-implemented per search
/// provider.
///
/// <para>
/// <b>Why a structural fence and not just behaviour tests.</b> The behaviour tests in
/// <c>WebFetchToolSanitizationTests</c> / <c>WebSearchToolSanitizationTests</c> prove the output is
/// clean today. They cannot prove HOW it got clean. A future change that "fixed" a newly discovered
/// marker by adding a strip inside <c>BraveSearchProvider</c> would keep every behaviour test green
/// while creating a second definition of what a marker looks like — and the next marker would then
/// be added to only one of them. That is not hypothetical here: the size-cap decision is already
/// copied verbatim into four providers, which is exactly what the issue cites as evidence that the
/// hazard was recognised four times and answered inconsistently. This fence makes the fifth copy
/// fail the build instead of shipping.
/// </para>
/// </summary>
public sealed class WebToolSanitizationBoundaryArchitectureTests : ArchitectureTest
{

    private string WebToolsRoot =>
        Path.Combine(Repository.Root, "src", "extensions", "BotNexus.Extensions.WebTools");

    private string SearchProvidersRoot => Path.Combine(WebToolsRoot, "Search");

    /// <summary>Non-vacuity guard: the fence is worthless if it is scanning an empty tree.</summary>
    [Fact]
    public void WebToolsProject_AndItsSearchProviders_Exist()
    {
        Directory.Exists(WebToolsRoot).ShouldBeTrue($"WebTools project not found at {WebToolsRoot}");
        Directory.Exists(SearchProvidersRoot).ShouldBeTrue(
            $"Search provider directory not found at {SearchProvidersRoot}");

        var providers = Directory.GetFiles(SearchProvidersRoot, "*SearchProvider.cs");
        providers.Length.ShouldBeGreaterThanOrEqualTo(
            4,
            "Expected the four+ search providers this fence exists to constrain. Found: " +
            string.Join(", ", providers.Select(Path.GetFileName)));
    }

    /// <summary>
    /// AC3: no search provider may invoke the sanitizer. Providers are a transport/parse contract;
    /// sanitization belongs to the tool that owns the output boundary.
    /// </summary>
    [Fact]
    public void SearchProviders_DoNotInvokeTheSanitizer()
    {
        var offenders = Directory
            .GetFiles(SearchProvidersRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file => StripComments(File.ReadAllText(file)).Contains(
                "UntrustedContentSanitizer", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToArray();

        offenders.ShouldBeEmpty(
            "Search providers must NOT call UntrustedContentSanitizer (#2813 AC3). Sanitization is " +
            "applied once at the WebSearchTool output boundary so every provider - including ones " +
            "added later - is covered without restating the decision. Offenders: " +
            string.Join(", ", offenders));
    }

    /// <summary>
    /// AC3: no provider may hand-roll its own marker stripping either. Calling the canonical
    /// sanitizer is not the only way to duplicate the decision - open-coding a regex over
    /// <c>&lt;|...|&gt;</c> or <c>&lt;system&gt;</c> creates the same second spelling.
    /// </summary>
    [Fact]
    public void SearchProviders_DoNotHandRollMarkerStripping()
    {
        // Marker-shaped literals that only appear in code whose purpose is to recognise injection
        // markup. Deliberately narrow: matching bare "<" or "|" would fire on ordinary parsing.
        // The delimiter class covers the ASCII pipe AND the fullwidth pipe U+FF5C so a provider that
        // hand-rolled the fullwidth spelling is caught too, and so this assertion cannot drift apart
        // from UntrustedContentSanitizer.SpecialTokenPattern (#3682 AC6).
        var markerShapes = new Regex(
            "im_start|im_end|endoftext|reserved_special_token|<\\s*[|\uFF5C].*[|\uFF5C]\\s*>|</?\\s*(?:system|assistant|tool_call|tool_use|function_calls)\\s*>",
            RegexOptions.IgnoreCase);

        var offenders = new List<string>();
        foreach (var file in Directory.GetFiles(SearchProvidersRoot, "*.cs", SearchOption.AllDirectories))
        {
            var code = StripComments(File.ReadAllText(file));
            if (markerShapes.IsMatch(code))
            {
                offenders.Add(Path.GetFileName(file));
            }
        }

        offenders.ShouldBeEmpty(
            "Search providers must NOT hand-roll injection-marker stripping (#2813 AC3). The " +
            "definition of 'what a marker looks like' lives once, in UntrustedContentSanitizer, " +
            "and is consumed - never restated. Offenders: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// AC1/AC2/AC3: each tool applies the sanitizer, and applies it exactly once. A second call in
    /// the same file is not harmless belt-and-braces - it means two places now decide when output
    /// is sanitized, and one of them will be missed by the next change.
    /// </summary>
    [Theory]
    [InlineData("WebFetchTool.cs")]
    [InlineData("WebSearchTool.cs")]
    public void EachWebTool_InvokesTheSanitizerExactlyOnce(string toolFileName)
    {
        var path = Path.Combine(WebToolsRoot, toolFileName);
        File.Exists(path).ShouldBeTrue($"{toolFileName} not found at {path}");

        var code = StripComments(File.ReadAllText(path));
        var callCount = Regex.Matches(code, @"UntrustedContentSanitizer\s*\.\s*Sanitize\s*\(").Count;

        callCount.ShouldBe(
            1,
            $"{toolFileName} must call UntrustedContentSanitizer.Sanitize exactly once, at the " +
            "single boundary where external content becomes tool output (#2813). Found " +
            $"{callCount} call(s).");
    }

    /// <summary>
    /// The sanitizer must be the shared canonical one from <c>BotNexus.Domain.Text</c>, not a copy
    /// re-declared inside the extension. A local re-declaration would satisfy every other test here
    /// while reintroducing precisely the duplication this issue exists to remove.
    /// </summary>
    [Fact]
    public void WebTools_DoesNotDeclareItsOwnSanitizer()
    {
        var offenders = Directory
            .GetFiles(WebToolsRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
            .Where(file => Regex.IsMatch(
                StripComments(File.ReadAllText(file)),
                @"(class|record|struct)\s+\w*(Sanitizer|ContentSanitizer)\b"))
            .Select(Path.GetFileName)
            .ToArray();

        offenders.ShouldBeEmpty(
            "BotNexus.Extensions.WebTools must CONSUME UntrustedContentSanitizer from " +
            "BotNexus.Domain.Text, not declare a sanitizer of its own (#2813). Offenders: " +
            string.Join(", ", offenders));
    }

    /// <summary>
    /// Removes comment text before scanning. Without this the fence would fire on its own
    /// explanatory comments in the tools (which legitimately name <c>&lt;system&gt;</c> and
    /// <c>&lt;|im_start|&gt;</c> to explain what is being defended against), and a provider could
    /// equally hide a violation from the count by trailing a call with a comment.
    /// </summary>
    private static string StripComments(string code)
    {
        code = Regex.Replace(code, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        code = Regex.Replace(code, @"(?m)//.*$", string.Empty);
        return code;
    }

}
