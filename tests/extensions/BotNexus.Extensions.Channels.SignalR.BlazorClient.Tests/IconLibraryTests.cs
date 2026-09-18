using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
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
    private static readonly string s_cssPath = Path.Combine(s_outputPath, "wwwroot", "css", "app.css");
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
