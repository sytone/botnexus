using System.Text.RegularExpressions;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// #2606: <c>Capabilities</c> on the satellite types is a <b>display-only</b> field. There is no
/// dispatch surface over <see cref="ISatelliteRegistry"/>-shaped APIs for it to gate, so it carries
/// no enforcement semantics and must not be mistaken for an authorization control.
///
/// <para>Two fences keep that statement true:</para>
/// <list type="number">
/// <item><b>Rule 1</b> — every satellite-type <c>.Capabilities</c> read in <c>src</c> stays inside the
/// known populate/display allowlist. A new consumer that is NOT populate-or-display is either a real
/// enforcement seam (in which case the display-only XML doc is now a lie and must be replaced by a
/// shared authorizer per #2606 AC1–AC3) or an accidental read of an unenforced field. Either way the
/// build must stop and a human must decide.</item>
/// <item><b>Rule 2</b> — the XML doc on each declaration keeps stating the display-only posture and the
/// explicit empty-list meaning (#2606 AC3/AC4). Deleting the disclaimer silently restores the false
/// impression of a control.</item>
/// </list>
/// </summary>
public sealed class SatelliteCapabilityEnforcementFenceTests : ArchitectureTest
{
    /// <summary>
    /// Known, reviewed satellite-type <c>.Capabilities</c> consumers, each classified. All are
    /// population (config -> model) or display (model -> DTO / table cell). None enforce.
    /// Adding to this list is a deliberate act that must be justified in review.
    /// </summary>
    private static readonly string[] s_allowedConsumers =
    [
        // populate: SatelliteConfig -> SatelliteConnectionInfo
        Path.Combine("gateway", "BotNexus.Gateway", "Satellites", "InMemorySatelliteRegistry.cs"),
        // populate: SatelliteConfig -> Satellite (world descriptor)
        Path.Combine("gateway", "BotNexus.Gateway.Configuration", "WorldDescriptorBuilder.cs"),
        // display: SatelliteConnectionInfo -> HTTP response DTO
        Path.Combine("gateway", "BotNexus.Gateway.Api", "Controllers", "SatellitesController.cs"),
        // display: SatelliteConfig -> CLI table cell
        Path.Combine("gateway", "BotNexus.Cli", "Commands", "SatelliteCommand.cs"),
    ];

    /// <summary>Declarations that must carry the display-only disclaimer.</summary>
    private static readonly string[] s_declarations =
    [
        Path.Combine("domain", "BotNexus.Domain", "World", "Satellite.cs"),
        Path.Combine("gateway", "BotNexus.Gateway.Abstractions", "Satellites", "SatelliteConnectionInfo.cs"),
        Path.Combine("gateway", "BotNexus.Gateway.Configuration", "SatelliteConfig.cs"),
    ];

    /// <summary>
    /// Matches a read of <c>.Capabilities</c> off an identifier whose name suggests a satellite
    /// (sat, satellite, satConfig, s.Capabilities inside a satellite file). Kept deliberately broad
    /// on the satellite source files and narrow elsewhere to avoid matching Copilot/Ollama/MCP/Spectre
    /// capability types, which are unrelated.
    /// </summary>
    private static readonly Regex s_satelliteCapabilityRead =
        new(@"\b(sat|satellite|satConfig|satelliteInfo|connectionInfo)\w*\??\.Capabilities\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [Fact]
    public void Rule1_NoSatelliteCapabilityConsumerOutsideTheReviewedAllowlist()
    {
        var srcRoot = Repository.SourceRoot;
        var violations = new List<string>();

        foreach (var file in Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(srcRoot, file);
            if (s_allowedConsumers.Any(a => relative.EndsWith(a, StringComparison.OrdinalIgnoreCase)))
                continue;

            var text = File.ReadAllText(file);
            foreach (Match m in s_satelliteCapabilityRead.Matches(text))
            {
                violations.Add($"{relative}: '{m.Value}'");
            }
        }

        Assert.True(
            violations.Count == 0,
            "#2606: a new satellite Capabilities consumer appeared outside the reviewed allowlist:\n  "
            + string.Join("\n  ", violations)
            + "\n\nSatellite Capabilities is documented as DISPLAY-ONLY with no enforcement semantics. "
            + "If this new site makes an authorization decision, the XML doc is now wrong: introduce a "
            + "single shared authorizer (#2606 AC1-AC3), log refusals with satellite id and capability, "
            + "and update the declarations' XML docs. If it is purely populate/display, add it to "
            + "s_allowedConsumers with its classification.");
    }

    [Fact]
    public void Rule2_DeclarationsRetainTheDisplayOnlyDisclaimer()
    {
        var srcRoot = Repository.SourceRoot;
        var missing = new List<string>();

        foreach (var declaration in s_declarations)
        {
            var path = Path.Combine(srcRoot, declaration);
            Assert.True(File.Exists(path), $"#2606 fence points at a missing file: {declaration}");

            var text = File.ReadAllText(path);
            if (!text.Contains("display-only", StringComparison.OrdinalIgnoreCase)
                || !text.Contains("#2606", StringComparison.Ordinal))
            {
                missing.Add(declaration);
            }
        }

        Assert.True(
            missing.Count == 0,
            "#2606: these satellite Capabilities declarations lost the display-only / no-enforcement "
            + "disclaimer (must mention 'display-only' and '#2606'):\n  " + string.Join("\n  ", missing)
            + "\n\nWithout it an operator reading the CLI table or the /api/satellites response "
            + "reasonably concludes the list constrains what the satellite may be asked to do. It does not.");
    }

}
