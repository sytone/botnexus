using System.Reflection;
using System.Text.RegularExpressions;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Keeps SQLite conversation row mapping on its reader subject and guards the extension-method
/// surface against accidental duplicate declarations.
/// </summary>
public sealed class SqliteDataReaderExtensionsArchitectureTests : ArchitectureTest
{
    private static readonly string[] MapperNames =
    [
        "MapConversation",
        "MapSummary",
        "MapParticipant",
        "MapParticipantSummary",
        "MapBinding"
    ];

    [Fact]
    public void ConversationMappers_AreDeclaredAsReaderExtensions_WithoutNameCollisions()
    {
        var conversationsPath = Repository.Path("src", "gateway", "BotNexus.Gateway.Conversations");
        var extensionPath = Repository.Path("src", "gateway", "BotNexus.Gateway.Conversations", "SqliteDataReaderExtensions.cs");
        var legacyPath = Repository.Path("src", "gateway", "BotNexus.Gateway.Conversations", "ConversationRowMapper.cs");

        File.Exists(extensionPath).ShouldBeTrue("The SQLite row extensions must live in their specifically named source file.");
        File.Exists(legacyPath).ShouldBeFalse("The old ConversationRowMapper file must be removed, not retained as a parallel helper.");

        var source = File.ReadAllText(extensionPath);
        source.Contains("namespace BotNexus.Gateway.Conversations;", StringComparison.Ordinal).ShouldBeTrue();
        source.Contains("internal static class SqliteDataReaderExtensions", StringComparison.Ordinal).ShouldBeTrue();

        foreach (var mapperName in MapperNames)
        {
            var declaration = new Regex(
                $@"\binternal\s+static\s+[^;={{}}]+\b{mapperName}\s*\(\s*this\s+SqliteDataReader\s+reader\b",
                RegexOptions.CultureInvariant);
            declaration.IsMatch(source).ShouldBeTrue($"{mapperName} must be an extension on SqliteDataReader.");

            var declarations = Directory.EnumerateFiles(conversationsPath, "*.cs", SearchOption.AllDirectories)
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
    public void SqliteConversationStore_UsesReaderSubjectCallSites()
    {
        var storePath = Repository.Path("src", "gateway", "BotNexus.Gateway.Conversations", "SqliteConversationStore.cs");
        var source = File.ReadAllText(storePath);

        source.Contains("ConversationRowMapper.", StringComparison.Ordinal).ShouldBeFalse();
        foreach (var mapperName in MapperNames)
        {
            source.Contains($"reader.{mapperName}(", StringComparison.Ordinal).ShouldBeTrue($"SqliteConversationStore must call {mapperName} on the reader subject.");
        }
    }
}
