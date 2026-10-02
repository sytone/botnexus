using System.Text.RegularExpressions;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Ratchets production configuration access toward the home-rooted, SQLite-first provider and
/// writer composition (#4567). Existing debt is counted exactly per file and rule: it may shrink,
/// but a new occurrence cannot hide inside an already-baselined file.
/// </summary>
public sealed class ConfigurationAuthorityFenceArchitectureTests : ArchitectureTest
{
    private const string BaselineFileName = "ConfigurationAuthorityBaseline.baseline";
    private const int ExpectedBaselineEntryCount = 89;
    private const int ExpectedBaselineOccurrenceCount = 219;

    private static readonly Rule[] Rules =
    [
        new("config-json", new Regex(@"config\.json", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
        new("direct-writer", new Regex(@"\bnew\s+(?:JsonConfigurationWriter|SqliteConfigurationWriter|PlatformConfigWriter|FanOutConfigurationWriter)\s*\(", RegexOptions.Compiled | RegexOptions.CultureInvariant)),
        new("backend-selector", new Regex(@"\b(?:preserveStoreOnly|writeJson|useSqlite)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
    ];

    [Fact]
    public void ProductionConfigurationAuthorityDebt_DoesNotGrowOrGoStale()
    {
        var actual = ScanProductionSource();
        var baseline = ReadBaseline();

        actual.Count.ShouldBe(ExpectedBaselineEntryCount,
            "the authority fence must find every reviewed file/rule debt entry; lower the explicit ceiling when debt shrinks");
        actual.Values.Sum().ShouldBe(ExpectedBaselineOccurrenceCount,
            "the checked-in occurrence ceiling is explicit; lower it when migration removes debt");
        baseline.Count.ShouldBe(ExpectedBaselineEntryCount,
            "the checked-in entry ceiling is explicit; baseline entries may only be removed");
        baseline.Values.Sum().ShouldBe(ExpectedBaselineOccurrenceCount,
            "the baseline occurrence total must match the reviewed ceiling");

        var newOrExpanded = actual
            .Where(pair => !baseline.TryGetValue(pair.Key, out var ceiling) || pair.Value > ceiling)
            .Select(pair => $"{pair.Key}: actual {pair.Value}, ceiling {(baseline.TryGetValue(pair.Key, out var ceiling) ? ceiling : 0)}")
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        newOrExpanded.ShouldBeEmpty(
            "new config.json authority, direct writer construction, or caller-selected backend composition is forbidden. " +
            "Use a verified BotNexus home/configuration context and the canonical provider/writer. Do not increase the baseline.\n  " +
            string.Join("\n  ", newOrExpanded));

        var stale = baseline
            .Where(pair => !actual.TryGetValue(pair.Key, out var count) || count < pair.Value)
            .Select(pair => $"{pair.Key}: actual {(actual.TryGetValue(pair.Key, out var count) ? count : 0)}, baseline {pair.Value}")
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        stale.ShouldBeEmpty(
            "configuration authority debt was removed. Delete or lower these baseline entries and reduce the explicit totals.\n  " +
            string.Join("\n  ", stale));
    }

    [Fact]
    public void Fence_ScansTheCompleteProductionTree()
    {
        var files = ProductionSourceFiles().ToArray();
        files.Length.ShouldBeGreaterThan(500);
        files.ShouldContain(file => file.EndsWith("GatewayCommand.cs", StringComparison.Ordinal));
        files.ShouldContain(file => file.EndsWith("ConfigWriterFactory.cs", StringComparison.Ordinal));
    }

    [Fact]
    public void Fence_RejectsKnownLeakShapesAndAcceptsCanonicalConsumers()
    {
        FindRules("Path.Combine(home, \"config.json\")").ShouldContain("config-json");
        FindRules("new JsonConfigurationWriter(path, fileSystem, backup)").ShouldContain("direct-writer");
        FindRules("preserveStoreOnly: true").ShouldContain("backend-selector");

        FindRules("IPlatformConfigAccessor accessor = services.GetRequiredService<IPlatformConfigAccessor>();").ShouldBeEmpty();
        FindRules("IConfigurationWriter writer = services.GetRequiredService<IConfigurationWriter>();").ShouldBeEmpty();
        FindRules("var context = BotNexusHome.ResolveHomePath();").ShouldBeEmpty();
    }

    [Fact]
    public void Baseline_IsWellFormedSortedAndNamesRealFiles()
    {
        var path = Path.Combine(AppContext.BaseDirectory, BaselineFileName);
        var lines = File.ReadAllLines(path)
            .Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith('#'))
            .ToArray();
        var sorted = lines.OrderBy(line => line, StringComparer.OrdinalIgnoreCase).ToArray();
        lines.SequenceEqual(sorted, StringComparer.OrdinalIgnoreCase).ShouldBeTrue(
            "baseline lines must be sorted case-insensitively for stable cross-platform review");

        var productionFiles = ProductionSourceFiles().ToHashSet(StringComparer.Ordinal);
        foreach (var key in ReadBaseline().Keys)
        {
            var separator = key.IndexOf('|');
            separator.ShouldBeGreaterThan(0);
            Rules.Select(rule => rule.Name).ShouldContain(key[..separator]);
            productionFiles.ShouldContain(key[(separator + 1)..], "every baseline entry must name a current production file");
        }
    }

    private Dictionary<string, int> ScanProductionSource()
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var relativePath in ProductionSourceFiles())
        {
            var text = File.ReadAllText(Path.Combine(Repository.Root, relativePath));
            foreach (var rule in Rules)
            {
                var count = rule.Pattern.Matches(text).Count;
                if (count > 0)
                    result.Add($"{rule.Name}|{relativePath}", count);
            }
        }

        return result;
    }

    private IEnumerable<string> ProductionSourceFiles() =>
        Directory.EnumerateFiles(Repository.SourceRoot, "*.cs", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(Repository.Root, file).Replace('\\', '/'))
            .Where(path => !path.Contains("/obj/", StringComparison.Ordinal) && !path.Contains("/bin/", StringComparison.Ordinal))
            .OrderBy(path => path, StringComparer.Ordinal);

    private static string[] FindRules(string source) => Rules
        .Where(rule => rule.Pattern.IsMatch(source))
        .Select(rule => rule.Name)
        .ToArray();

    private static Dictionary<string, int> ReadBaseline()
    {
        var path = Path.Combine(AppContext.BaseDirectory, BaselineFileName);
        File.Exists(path).ShouldBeTrue($"{BaselineFileName} must be copied to the architecture test output");
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
                continue;

            var lastSeparator = line.LastIndexOf('|');
            lastSeparator.ShouldBeGreaterThan(0, $"Malformed baseline line: {line}");
            int.TryParse(line[(lastSeparator + 1)..], out var count).ShouldBeTrue($"Malformed baseline count: {line}");
            count.ShouldBeGreaterThan(0);
            result.Add(line[..lastSeparator], count);
        }

        return result;
    }

    private sealed record Rule(string Name, Regex Pattern);
}
