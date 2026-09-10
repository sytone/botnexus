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
    private static readonly string s_cssPath = Path.Combine(
        Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!,
        "wwwroot", "css", "app.css");

    private static readonly Regex s_id = new(@"\bid=""([^""]+)""", RegexOptions.Compiled);
    private static readonly Regex s_urlRef = new(@"url\(#([^)]+)\)", RegexOptions.Compiled);

    [Fact]
    public void EveryIconInTheSetIsExposed()
    {
        Assert.NotEmpty(IconLibrary.Names);
        Assert.Equal(IconLibrary.Names.Count, IconLibrary.Icons.Count);
        Assert.All(IconLibrary.Names, n => Assert.True(IconLibrary.Icons.ContainsKey(n), n));
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
        // A hard-coded stroke cannot answer a hover, disabled or selected state. The generator
        // moves flat colours out to a CSS tone and leaves currentColor behind; only a gradient
        // is allowed to name itself, and .bn-icon-flat exists to override that one.
        foreach (var (name, def) in IconLibrary.Icons)
        {
            var ok = def.Stroke.Equals("currentColor", StringComparison.Ordinal)
                     || def.Stroke.StartsWith("url(#", StringComparison.Ordinal);
            Assert.True(ok, $"'{name}' strokes with '{def.Stroke}', which no rule can override.");
        }
    }

    [Fact]
    public void ToneOverridesAreDeclaredAfterThePerIconTones()
    {
        // .bn-icon-inherit and .bn-icon-<name> are both single-class selectors, so source order
        // is the only thing deciding which wins. Written above the tones they silently lose.
        var css = File.ReadAllText(s_cssPath);

        var lastTone = css.LastIndexOf(".bn-icon-activity {", StringComparison.Ordinal);
        var inherit = css.IndexOf(".bn-icon-inherit {", StringComparison.Ordinal);
        var flat = css.IndexOf(".bn-icon-flat {", StringComparison.Ordinal);

        Assert.True(lastTone >= 0, "no generated per-icon tone found");
        Assert.True(inherit > lastTone, ".bn-icon-inherit must be declared after the per-icon tones");
        Assert.True(flat > lastTone, ".bn-icon-flat must be declared after the per-icon tones");
    }

    [Fact]
    public void EveryTonedIconHasACssRule()
    {
        var css = File.ReadAllText(s_cssPath);

        foreach (var (name, def) in IconLibrary.Icons)
        {
            if (def.Stroke.StartsWith("url(#", StringComparison.Ordinal))
                continue;

            // currentColor icons intentionally have no tone: they inherit their context.
            var hasRule = css.Contains($".bn-icon-{name} {{ color:", StringComparison.Ordinal);
            var body = IconLibrary.Icons[name].Body;
            Assert.True(hasRule || !body.Contains("stop-color", StringComparison.Ordinal),
                $"'{name}' has no tone rule and no gradient.");
        }
    }
}
