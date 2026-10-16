using System.Reflection;
using System.Text.RegularExpressions;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Keeps SQLite session row mapping on its reader subject and guards the extension-method
/// surface against accidental duplicate declarations or instance-member shadowing.
/// </summary>
public sealed class SqliteSessionDataReaderExtensionsArchitectureTests : ArchitectureTest
{
    private static readonly string[] MapperNames =
    [
        "MapSession",
        "MapHistoryEntry",
        "MapSummaryRow",
        "MapSubAgentSession"
    ];

    [Fact]
    public void SessionMappers_AreDeclaredAsReaderExtensions_WithoutNameCollisions()
    {
        var sessionsPath = Repository.Path("src", "gateway", "BotNexus.Gateway.Sessions");
        var extensionPath = Repository.Path("src", "gateway", "BotNexus.Gateway.Sessions", "SqliteDataReaderExtensions.cs");
        var legacyPath = Repository.Path("src", "gateway", "BotNexus.Gateway.Sessions", "SessionRowMapper.cs");

        File.Exists(extensionPath).ShouldBeTrue("The SQLite row extensions must live in their specifically named source file.");
        File.Exists(legacyPath).ShouldBeFalse("The old SessionRowMapper file must be removed, not retained as a parallel helper.");

        var source = File.ReadAllText(extensionPath);
        source.Contains("namespace BotNexus.Gateway.Sessions;", StringComparison.Ordinal).ShouldBeTrue();
        source.Contains("internal static class SqliteDataReaderExtensions", StringComparison.Ordinal).ShouldBeTrue();

        foreach (var mapperName in MapperNames)
        {
            var declaration = new Regex(
                $@"\binternal\s+static\s+[^;={{}}]+\b{mapperName}\s*\(\s*this\s+SqliteDataReader\s+reader\b",
                RegexOptions.CultureInvariant);
            declaration.IsMatch(source).ShouldBeTrue($"{mapperName} must be an extension on SqliteDataReader.");

            var declarations = Directory.EnumerateFiles(sessionsPath, "*.cs", SearchOption.AllDirectories)
                .SelectMany(path => Regex.Matches(
                    File.ReadAllText(path),
                    $@"\bstatic\s+[^;={{}}]+\b{mapperName}\s*\(\s*this\s+SqliteDataReader\s+",
                    RegexOptions.CultureInvariant).Select(_ => path))
                .ToArray();
            declarations.Length.ShouldBe(1, $"{mapperName} must have exactly one SqliteDataReader extension declaration: {string.Join(", ", declarations)}");
            Path.GetFullPath(declarations[0]).ShouldBe(Path.GetFullPath(extensionPath));
        }
    }

    [Fact]
    public void MapperNames_DoNotCollideWithReaderInstanceMembers()
    {
        var instanceNames = typeof(Microsoft.Data.Sqlite.SqliteDataReader)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(method => method.Name)
            .ToHashSet(StringComparer.Ordinal);

        MapperNames.Where(instanceNames.Contains).ShouldBeEmpty(
            "An instance method shadows an extension without a compiler error; rename the extension or retain a documented static exception.");
    }

    [Fact]
    public void SqliteSessionStore_UsesReaderSubjectCallSites()
    {
        var storePath = Repository.Path("src", "gateway", "BotNexus.Gateway.Sessions", "SqliteSessionStore.cs");
        var source = File.ReadAllText(storePath);

        source.Contains("SessionRowMapper.", StringComparison.Ordinal).ShouldBeFalse();
        foreach (var mapperName in MapperNames)
        {
            var receiver = mapperName == "MapHistoryEntry" ? "historyReader" : "reader";
            source.Contains($"{receiver}.{mapperName}(", StringComparison.Ordinal)
                .ShouldBeTrue($"SqliteSessionStore must call {mapperName} on the reader subject.");
        }
    }
}
