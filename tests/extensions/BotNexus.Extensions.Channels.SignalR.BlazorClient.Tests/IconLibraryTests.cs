using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Fences on the generated icon set (assets/icons/svg -> IconLibrary.g.cs).
///
/// The set arrived with every gradient declared as <c>id="g"</c>. SVG ids are document-global,
/// so once two of those icons rendered together every <c>url(#g)</c> resolved to whichever
/// landed in the DOM first and the icons silently took each other's colours - three of them
/// share the sidebar. Nothing about that fails loudly, which is exactly why it needs a test:
/// the generator makes the ids unique, and these assert it stayed that way.
/// </summary>
public sealed class IconLibraryTests
{
    private static readonly string s_outputPath =
        Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
    private static readonly string s_repoRoot = Path.GetFullPath(Path.Combine(
        s_outputPath, "..", "..", "..", "..", "..", ".."));
    private static readonly string s_cssPath = Path.Combine(
        s_repoRoot, "src", "extensions", "BotNexus.Extensions.Channels.SignalR.BlazorClient.Core",
        "wwwroot", "css", "icons.css");
    private static readonly string s_iconReadmePath = Path.Combine(s_outputPath, "assets", "icons", "README.md");
    private static readonly string s_svgPath = Path.Combine(s_outputPath, "assets", "icons", "svg");

    private static readonly Regex s_id = new(@"\bid=""([^""]+)""", RegexOptions.Compiled);
    private static readonly Regex s_gradientId = new(
        @"<(?:linear|radial)Gradient\b[^>]*\bid=""([^""]+)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex s_urlRef = new(@"url\(#([^)]+)\)", RegexOptions.Compiled);
    private static readonly Regex s_sourceStroke = new(
        @"<svg\b[^>]*\bstroke=""([^""]+)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex s_strokeAttribute = new(
        @"\bstroke\s*=\s*[""'](?<value>[^""']+)[""']",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex s_styleAttribute = new(
        @"\bstyle\s*=\s*[""'](?<value>[^""']*)[""']",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex s_styleStroke = new(
        @"(?:^|;)\s*stroke\s*:\s*(?<value>[^;]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex s_stopColor = new(
        @"\bstop-color=""([^""]+)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    [Theory]
    [InlineData("BotNexus.Extensions.Channels.SignalR.BlazorClient")]
    [InlineData("BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile")]
    public void EveryClientLoadsSharedIconStylesExactlyOnceWithoutPrivateCopies(string clientProject)
    {
        var projectRoot = Path.Combine(s_repoRoot, "src", "extensions", clientProject);
        var indexPath = Path.Combine(projectRoot, "wwwroot", "index.html");
        SharedIconLinkErrors(indexPath, File.ReadAllText(indexPath)).ShouldBeEmpty();

        // Do not limit ownership to index.html links: unlinked files and isolated .razor.css
        // can become active without changing the entry point. Exclude only generated output.
        var cssFiles = Directory.EnumerateFiles(projectRoot, "*.css", SearchOption.AllDirectories)
            .Where(path => IsClientSourceCss(projectRoot, path))
            .ToArray();
        cssFiles.ShouldNotBeEmpty();
        cssFiles.SelectMany(path => PrivateIconRuleErrors(
                Path.GetRelativePath(s_repoRoot, path), File.ReadAllText(path)))
            .ToArray().ShouldBeEmpty();
    }

    private const string SharedIconHref =
        "_content/BotNexus.Extensions.Channels.SignalR.BlazorClient.Core/css/icons.css";

    [Theory]
    [InlineData("")]
    [InlineData("<!-- <link rel='stylesheet' href='{0}'> -->")]
    [InlineData("<link rel='preload' href='{0}'>")]
    [InlineData("<link rel='stylesheet' href='{0}'><link href='{0}?v=2' rel='stylesheet'>")]
    public void SharedIconLinkErrors_MissingOrDuplicateLink_ReportsPathAndCount(string template)
    {
        var errors = SharedIconLinkErrors("mobile/index.html", template.Replace("{0}", SharedIconHref));

        errors.ShouldHaveSingleItem().ShouldContain("mobile/index.html");
        errors[0].ShouldContain("found " + (template.Contains("?v=2", StringComparison.Ordinal) ? "2" : "0"));
    }

    [Theory]
    [InlineData("<link href='{0}' rel='stylesheet'>")]
    [InlineData("<!-- <link rel='stylesheet' href='{0}'> --><LINK HREF=\"{0}?v=3#icons\" REL='alternate stylesheet'>")]
    [InlineData("<link rel=stylesheet href={0}>")]
    public void SharedIconLinkErrors_OneRealLink_AcceptsHtmlAttributeVariants(string template)
    {
        SharedIconLinkErrors("desktop/index.html", template.Replace("{0}", SharedIconHref)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(".bn-icon { display: inline-block; }", ".bn-icon")]
    [InlineData(".other, .bn-icon { display: block; }", ".bn-icon")]
    [InlineData(".toolbar .bn-icon, .bn-icon { margin: 0; }", ".bn-icon")]
    [InlineData("svg.bn-icon { flex-shrink: 0; }", "svg.bn-icon")]
    [InlineData(".other, .bn-icon:hover { vertical-align: -0.125em; }", ".bn-icon:hover")]
    [InlineData("@media (prefers-reduced-motion: reduce) { .bn-icon-spin { animation: none; } }", ".bn-icon-spin")]
    [InlineData("@supports (display: grid) { .toolbar .bn-icon-spin:hover { animation-name: bn-icon-spin; } }", ".toolbar .bn-icon-spin:hover")]
    [InlineData(".bn-icon-flat:is(svg, span) { stroke: currentColor; }", ".bn-icon-flat:is(svg, span)")]
    [InlineData(".bn-icon-inherit::before { color: inherit; }", ".bn-icon-inherit::before")]
    [InlineData(".selected.bn-icon-chat { color: #3B82F6; }", ".selected.bn-icon-chat")]
    [InlineData(":is(.bn-icon, .other) { display: inline-block; }", ":is(.bn-icon, .other)")]
    [InlineData(".toolbar { .bn-icon { color: red; } }", ".bn-icon")]
    [InlineData(".bn-icon { &:hover { color: red; } }", "&:hover")]
    [InlineData("@keyframes bn-icon-spin { to { transform: rotate(360deg); } }", "@keyframes bn-icon-spin")]
    public void PrivateIconRuleErrors_CopiedPresentation_ReportsPathAndSelector(string css, string selector)
    {
        var errors = PrivateIconRuleErrors("Components/Fixture.razor.css", css);

        errors.ShouldHaveSingleItem().ShouldContain("Components/Fixture.razor.css");
        errors[0].ShouldContain(selector);
    }

    [Theory]
    [InlineData(".composer-action-btn > .bn-icon { display: block; vertical-align: 0; }")]
    [InlineData(".toolbar .bn-icon { margin-inline: 4px; width: 1em; }")]
    [InlineData(".toolbar { & > .bn-icon { display: block; } }")]
    [InlineData("/* .bn-icon { color: red; } */ .other { color: red; }")]
    [InlineData(".other::before { content: '.bn-icon { color: red; }'; }")]
    [InlineData(".bn-iconic { color: red; } .other { --example: '.bn-icon'; }")]
    public void PrivateIconRuleErrors_ContextualLayoutOrNonRules_AreAllowed(string css)
    {
        PrivateIconRuleErrors("wwwroot/css/fixture.css", css).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(".bn-icon", "", "display", "inline-block")]
    [InlineData(".bn-icon", "", "flex-shrink", "0")]
    [InlineData(".bn-icon", "", "vertical-align", "-0.125em")]
    [InlineData(".bn-icon-spin", "", "animation", "bn-icon-spin 0.9s linear infinite")]
    [InlineData("to", "@keyframes bn-icon-spin", "transform", "rotate(360deg)")]
    [InlineData(".bn-icon-spin", "@media (prefers-reduced-motion: reduce)", "animation", "none")]
    [InlineData(".bn-icon-flat", "", "stroke", "currentColor")]
    [InlineData(".bn-icon-inherit", "", "color", "inherit")]
    [InlineData(".bn-icon-inherit", "", "stroke", "currentColor")]
    public void SharedIconStyles_ReservedPresentationRule_IsComplete(
        string selector, string scope, string property, string value)
    {
        var rules = ReadCssBlocks(File.ReadAllText(s_cssPath))
            .Where(rule => rule.Selector == selector && string.Join(" > ", rule.Parents) == scope)
            .ToArray();
        var rule = rules.ShouldHaveSingleItem();
        rule.Declarations.ShouldContain($"{property}: {value}");
    }

    private static string[] SharedIconLinkErrors(string path, string html)
    {
        using var document = new HtmlParser().ParseDocument(html);
        var count = document.QuerySelectorAll("link")
            .Where(link => (link.GetAttribute("rel") ?? "")
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Contains("stylesheet", StringComparer.OrdinalIgnoreCase))
            .Count(link => (link.GetAttribute("href") ?? "").Split('?', '#')[0] == SharedIconHref);
        return count == 1 ? [] : [$"{path}: expected exactly one shared icon stylesheet link; found {count}"];
    }

    [Fact]
    public void SharedCoreStyles_OutsideIconStylesheet_DoNotCopyIconPresentation()
    {
        var coreRoot = Path.GetDirectoryName(Path.GetDirectoryName(s_cssPath));
        coreRoot.ShouldNotBeNull();
        var projectRoot = Path.GetDirectoryName(coreRoot);
        projectRoot.ShouldNotBeNull();
        Directory.EnumerateFiles(projectRoot, "*.css", SearchOption.AllDirectories)
            .Where(path => path != s_cssPath && IsClientSourceCss(projectRoot, path))
            .SelectMany(path => PrivateIconRuleErrors(Path.GetRelativePath(s_repoRoot, path), File.ReadAllText(path)))
            .ToArray().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("wwwroot/css/unlinked.css", true)]
    [InlineData("Components/Fixture.razor.css", true)]
    [InlineData("obj/Debug/net10.0/scopedcss/Fixture.razor.rz.scp.css", false)]
    [InlineData("bin/Debug/net10.0/wwwroot/css/app.css", false)]
    public void IsClientSourceCss_UnlinkedAndIsolatedStyles_AreIncluded(string relativePath, bool expected)
    {
        var root = Path.Combine(Path.GetTempPath(), "icon-ownership-fixture");
        IsClientSourceCss(root, Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)))
            .ShouldBe(expected);
    }

    private static bool IsClientSourceCss(string projectRoot, string path) =>
        !Path.GetRelativePath(projectRoot, path)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(part => part is "bin" or "obj");

    private static readonly Regex s_reservedIconClass = new(
        @"\.bn-icon(?:-[a-zA-Z0-9_-]+)?(?![a-zA-Z0-9_-])", RegexOptions.Compiled);

    private static string[] PrivateIconRuleErrors(string path, string css) =>
        ReadCssBlocks(css).Where(rule =>
        {
            var selectors = rule.Parents.Where(parent => !parent.StartsWith('@'))
                .Append(rule.Selector);
            var effectiveSelector = string.Join(" ", selectors);
            if (rule.Parents.Append(rule.Selector).Any(selector =>
                    Regex.IsMatch(selector, @"^@(?:-webkit-)?keyframes\s+bn-icon-spin\s*$")))
                return rule.Declarations.Length > 0;
            if (!s_reservedIconClass.IsMatch(effectiveSelector) || rule.Declarations.Length == 0)
                return false;

            // Bare/compound/pseudo icon rules are shared-owned. Contextual layout may differ
            // (the composer uses display:block), but cannot recreate shared presentation.
            var contextual = SelectorArms(effectiveSelector)
                .Where(selector => s_reservedIconClass.IsMatch(selector))
                .All(HasContextCombinator);
            return !contextual || rule.Declarations.Any(declaration => !IsContextualLayout(declaration));
        }).Select(rule => $"{path}: shared icon presentation copied by "
            + string.Join(" > ", rule.Parents.Append(rule.Selector))).ToArray();

    private static bool IsContextualLayout(string declaration)
    {
        var parts = declaration.Split(':', 2);
        var property = parts[0].Trim().ToLowerInvariant();
        var value = parts[1].Trim();
        return (property == "display" && value.Equals("block", StringComparison.OrdinalIgnoreCase))
            || (property == "vertical-align" && value == "0")
            || property is "width" or "height" or "min-width" or "max-width" or "min-height" or "max-height"
                or "position" or "top" or "right" or "bottom" or "left" or "align-self" or "justify-self"
            || property == "margin" || property.StartsWith("margin-", StringComparison.Ordinal)
            || property == "padding" || property.StartsWith("padding-", StringComparison.Ordinal)
            || property == "inset" || property.StartsWith("inset-", StringComparison.Ordinal);
    }

    private static IEnumerable<string> SelectorArms(string selector)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i < selector.Length; i++)
        {
            if (selector[i] is '(' or '[') depth++;
            else if (selector[i] is ')' or ']') depth--;
            else if (depth == 0 && selector[i] == ',')
            {
                yield return selector[start..i].Trim();
                start = i + 1;
            }
        }
        yield return selector[start..].Trim();
    }

    private static bool HasContextCombinator(string selector)
    {
        var depth = 0;
        foreach (var character in selector.Trim())
        {
            if (character is '(' or '[') depth++;
            else if (character is ')' or ']') depth--;
            else if (depth == 0 && (char.IsWhiteSpace(character) || character is '>' or '+' or '~'))
                return true;
        }
        return false;
    }

    private sealed record CssBlock(string Selector, string[] Parents, string[] Declarations);

    // Small structural scanner, not a selector parser. It visits every nested block, keeping
    // only direct declarations; comments/quoted strings cannot manufacture selectors or braces.
    // Grouped and functional selectors stay intact so a reserved class in any arm is fenced.
    private static CssBlock[] ReadCssBlocks(string css)
    {
        var blocks = new List<CssBlock>();
        var stack = new Stack<(string Selector, List<string> Declarations)>();
        var text = new System.Text.StringBuilder();
        var depth = 0;
        char quote = '\0';
        for (var i = 0; i < css.Length; i++)
        {
            var character = css[i];
            if (quote != '\0')
            {
                text.Append(character);
                if (character == '\\' && i + 1 < css.Length) text.Append(css[++i]);
                else if (character == quote) quote = '\0';
                continue;
            }
            if (character == '/' && i + 1 < css.Length && css[i + 1] == '*')
            {
                var end = css.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) throw new InvalidDataException("Unterminated CSS comment");
                i = end + 1;
                continue;
            }
            if (character is '\'' or '"')
            {
                quote = character;
                text.Append(character);
                continue;
            }
            if (character is '(' or '[') depth++;
            if (character is ')' or ']') depth--;
            if (depth != 0 || character is not ('{' or '}' or ';'))
            {
                text.Append(character);
                continue;
            }
            var fragment = text.ToString().Trim();
            text.Clear();
            if (character == '{') stack.Push((fragment, []));
            else if (stack.Count > 0)
            {
                if (fragment.Contains(':', StringComparison.Ordinal))
                    stack.Peek().Declarations.Add(Regex.Replace(fragment, @"\s*:\s*", ": ", RegexOptions.None));
                if (character == '}')
                {
                    var frame = stack.Pop();
                    blocks.Add(new CssBlock(frame.Selector, stack.Reverse().Select(parent => parent.Selector).ToArray(),
                        frame.Declarations.ToArray()));
                }
            }
        }
        if (stack.Count != 0 || quote != '\0' || depth != 0)
            throw new InvalidDataException("Unbalanced CSS fixture/source");
        return blocks.ToArray();
    }

    [Fact]
    public void EveryIconInTheSetIsExposed()
    {
        Assert.NotEmpty(IconLibrary.Names);
        Assert.Equal(IconLibrary.Names.Count, IconLibrary.Icons.Count);
        Assert.All(IconLibrary.Names, n => Assert.True(IconLibrary.Icons.ContainsKey(n), n));
    }

    [Fact]
    public void AssetReadmeDescribesTheDeliveredDistribution()
    {
        var readme = File.ReadAllText(s_iconReadmePath);
        var sourceIconCount = Directory.EnumerateFiles(s_svgPath, "*.svg").Count();

        Assert.Contains($"set of {sourceIconCount} original", readme, StringComparison.Ordinal);
        Assert.Contains("`svg/`", readme, StringComparison.Ordinal);
        Assert.Contains("`preview.png`", readme, StringComparison.Ordinal);
        Assert.Contains("scripts/generate-icons.py", readme, StringComparison.Ordinal);
        Assert.Contains("`IconLibrary.g.cs`", readme, StringComparison.Ordinal);
        Assert.Contains("`Icon`", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("`png/`", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("`index.tsx`", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("## React", readme, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("import {", readme, StringComparison.Ordinal);
    }

    [Fact]
    public void NoTwoIconsDeclareTheSameId()
    {
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (name, def) in IconLibrary.Icons)
        {
            foreach (Match m in s_id.Matches(def.Body))
            {
                var id = m.Groups[1].Value;
                Assert.False(
                    owners.TryGetValue(id, out var first),
                    $"id '{id}' is declared by both '{first}' and '{name}'. Rendering both at once "
                    + "makes one of them resolve against the other's definition.");
                owners[id] = name;
            }
        }
    }

    [Fact]
    public void EveryReferencedIdIsDefinedByTheSameIcon()
    {
        foreach (var (name, def) in IconLibrary.Icons)
        {
            var defined = s_id.Matches(def.Body).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

            foreach (Match m in s_urlRef.Matches(def.Stroke + " " + def.Body))
            {
                Assert.Contains(m.Groups[1].Value, defined);
            }
        }
    }

    [Fact]
    public void EveryIconStrokeIsOverridableOrItsOwnGradient()
    {
        // Descendant presentation and style declarations override inheritance, so inspect them
        // alongside the generated root metadata rather than assuming the root describes all art.
        foreach (var (_, def) in IconLibrary.Icons)
        {
            Assert.Empty(UnsupportedStrokes(def.Stroke, def.Body));
        }
    }

    [Theory]
    [InlineData("<path stroke=\"#ff0000\" />")]
    [InlineData("<path style=\"fill: none; stroke: #ff0000; stroke-width: 2\" />")]
    public void DescendantHardCodedStrokeIsRejected(string body)
    {
        var unsupported = UnsupportedStrokes("currentColor", body);

        Assert.Single(unsupported);
        Assert.Equal("#ff0000", unsupported[0]);
    }

    [Fact]
    public void DescendantCurrentColorAndLocalGradientAreAccepted()
    {
        const string body = """
            <defs><linearGradient id="local"><stop stop-color="#fff" /></linearGradient></defs>
            <path stroke="currentColor" />
            <path style="stroke: url(#local)" />
            """;

        Assert.Empty(UnsupportedStrokes("currentColor", body));
    }

    [Fact]
    public void DescendantGradientMustBeDefinedByTheSameIcon()
    {
        var unsupported = UnsupportedStrokes("currentColor", "<path stroke=\"url(#other-icon)\" />");

        Assert.Single(unsupported);
        Assert.Equal("url(#other-icon)", unsupported[0]);
    }

    [Fact]
    public void ToneOverridesAreDeclaredAfterThePerIconTones()
    {
        // .bn-icon-inherit and .bn-icon-<name> are both single-class selectors, so source order
        // is the only thing deciding which wins. Compare against every expected source tone,
        // rather than one icon whose alphabetical position can stop being the final rule.
        var css = File.ReadAllText(s_cssPath);
        var expectedToneNames = ExpectedSourceTones().Select(tone => tone.Name).ToArray();
        Assert.NotEmpty(expectedToneNames);

        var tonePositions = expectedToneNames
            .Select(name => (Name: name, Position: css.IndexOf($".bn-icon-{name} {{", StringComparison.Ordinal)))
            .ToArray();
        Assert.All(tonePositions, tone => Assert.True(tone.Position >= 0, $"'{tone.Name}' has no CSS tone rule"));

        var lastTone = tonePositions.MaxBy(tone => tone.Position);
        var inherit = css.IndexOf(".bn-icon-inherit {", StringComparison.Ordinal);
        var flat = css.IndexOf(".bn-icon-flat {", StringComparison.Ordinal);

        Assert.True(inherit > lastTone.Position,
            $".bn-icon-inherit must be declared after final tone '{lastTone.Name}'");
        Assert.True(flat > lastTone.Position,
            $".bn-icon-flat must be declared after final tone '{lastTone.Name}'");
    }

    [Fact]
    public void EveryTonedIconHasACssRule()
    {
        var css = File.ReadAllText(s_cssPath);
        var sourceIcons = SourceIcons();
        var expectedTones = sourceIcons.Where(icon => icon.Tone is not null).ToArray();

        Assert.Contains(sourceIcons, icon => icon.Kind == SourceToneKind.Untoned);
        Assert.Contains(sourceIcons, icon => icon.Kind == SourceToneKind.Flat);
        Assert.Contains(sourceIcons, icon => icon.Kind == SourceToneKind.Gradient);
        foreach (var icon in expectedTones)
        {
            Assert.Contains($".bn-icon-{icon.Name} {{ color: {icon.Tone}; }}", css, StringComparison.Ordinal);
        }
    }

    private static string[] UnsupportedStrokes(string rootStroke, string body)
    {
        var definedIds = s_gradientId.Matches(body)
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
        var strokes = new List<string> { rootStroke };
        strokes.AddRange(s_strokeAttribute.Matches(body).Select(match => match.Groups["value"].Value));
        strokes.AddRange(
            s_styleAttribute.Matches(body)
                .SelectMany(style => s_styleStroke.Matches(style.Groups["value"].Value))
                .Select(match => match.Groups["value"].Value));

        return strokes
            .Select(stroke => stroke.Trim())
            .Where(stroke => !stroke.Equals("currentColor", StringComparison.OrdinalIgnoreCase))
            .Where(stroke => !IsLocalGradient(stroke, definedIds))
            .ToArray();
    }

    private static bool IsLocalGradient(string stroke, IReadOnlySet<string> definedIds)
    {
        var reference = s_urlRef.Match(stroke);
        return reference.Success
               && reference.Value.Equals(stroke, StringComparison.Ordinal)
               && definedIds.Contains(reference.Groups[1].Value);
    }

    private static (string Name, string Tone)[] ExpectedSourceTones() =>
        SourceIcons()
            .Where(icon => icon.Tone is not null)
            .Select(icon => (icon.Name, icon.Tone!))
            .ToArray();

    private static (string Name, string? Tone, SourceToneKind Kind)[] SourceIcons() =>
        Directory.EnumerateFiles(s_svgPath, "*.svg")
            .Select(path => (Name: Path.GetFileNameWithoutExtension(path), Svg: File.ReadAllText(path)))
            .Select(source =>
            {
                var stroke = SourceStroke(source.Name, source.Svg);
                if (stroke.Equals("currentColor", StringComparison.OrdinalIgnoreCase))
                    return (source.Name, Tone: (string?)null, Kind: SourceToneKind.Untoned);

                if (!stroke.StartsWith("url(#", StringComparison.Ordinal))
                    return (source.Name, Tone: stroke, Kind: SourceToneKind.Flat);

                var stop = s_stopColor.Match(source.Svg);
                Assert.True(stop.Success, $"gradient icon '{source.Name}' has no source stop colour");
                return (source.Name, Tone: (string?)stop.Groups[1].Value, Kind: SourceToneKind.Gradient);
            })
            .ToArray();

    private static string SourceStroke(string name, string svg)
    {
        var match = s_sourceStroke.Match(svg);
        Assert.True(match.Success, $"source icon '{name}' has no root stroke metadata");
        return match.Groups[1].Value;
    }

    private enum SourceToneKind
    {
        Untoned,
        Flat,
        Gradient,
    }
}
