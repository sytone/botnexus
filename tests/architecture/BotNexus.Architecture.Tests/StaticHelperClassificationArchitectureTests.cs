using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// #3114: freezes the reviewed classification of the four sealed framework subject clusters named
/// by the issue: JsonObject, JsonElement, SqliteConnection, and SqliteDataReader. The scanner uses
/// Roslyn syntax and symbols; the CSV is the review record rather than an executable heuristic.
/// </summary>
public sealed class StaticHelperClassificationArchitectureTests : ArchitectureTest
{
    private const string ArtifactName = "StaticHelperClassification.csv";

    [Fact]
    public void EveryCurrentCandidate_HasExactlyOneClassification_AndNoClassificationIsStale()
    {
        var rows = ReadRows();
        var current = Scan(includeExtensions: false).ToDictionary(item => item.Key, StringComparer.Ordinal);
        var classified = rows.Where(row => row.Disposition != "converted")
            .ToDictionary(row => row.Key, StringComparer.Ordinal);

        current.Keys.Except(classified.Keys, StringComparer.Ordinal).ShouldBeEmpty(
            "New non-private static helpers with a named, non-primitive, non-interface first parameter must be classified in the #3114 CSV.");
        classified.Keys.Except(current.Keys, StringComparer.Ordinal).ShouldBeEmpty(
            "Classification rows must describe checked-in current source; remove or update stale rows.");
    }

    [Fact]
    public void ConvertedRows_StillDescribeExtensionMethods()
    {
        var extensions = Scan(includeExtensions: true).Where(item => item.IsExtension)
            .ToDictionary(item => item.Key, StringComparer.Ordinal);
        var stale = ReadRows().Where(row => row.Disposition == "converted" && !extensions.ContainsKey(row.Key))
            .Select(row => row.Key);

        stale.ShouldBeEmpty("A 'converted' row must continue to identify an extension method in current source.");
    }

    [Fact]
    public void ClassificationArtifact_IsCompleteStableAndReviewable()
    {
        var rows = ReadRows();
        rows.ShouldNotBeEmpty();
        rows.Select(row => row.Key).ShouldBeUnique();
        rows.Select(row => row.Key).ShouldBe(rows.Select(row => row.Key).OrderBy(key => key, StringComparer.Ordinal).ToArray(),
            "CSV rows must use deterministic ordinal key order.");
        rows.ShouldAllBe(row => row.Category == "1" || row.Category == "3" || row.Category == "4");
        rows.ShouldAllBe(row => row.Disposition == "retained" || row.Disposition == "candidate" || row.Disposition == "converted");
        rows.ShouldAllBe(row => !string.IsNullOrWhiteSpace(row.Reason));
        rows.Where(row => row.Category == "4").ShouldAllBe(row => row.Disposition == "candidate" || row.Disposition == "converted");
        rows.Where(row => row.Category == "1" || row.Category == "3").ShouldAllBe(row => row.Disposition == "retained");
    }

    [Fact]
    public void Scanner_RecognizesCandidateAndExclusions()
    {
        const string source = """
            class JsonElement { }
            interface IBoundary { }
            static class Helpers
            {
                public static void Candidate(JsonElement subject) { }
                private static void Private(JsonElement subject) { }
                public static void Primitive(int value) { }
                public static void Interface(IBoundary boundary) { }
                public static void Extension(this JsonElement subject) { }
            }
            """;

        var scanned = ScanText(source, includeExtensions: false);
        scanned.Select(item => item.Method).ShouldBe(["Candidate"]);
        ScanText(source, includeExtensions: true).Select(item => item.Method).ShouldContain("Extension");
    }

    private IReadOnlyList<Row> ReadRows()
    {
        var path = Path.Combine(AppContext.BaseDirectory, ArtifactName);
        File.Exists(path).ShouldBeTrue($"The #3114 artifact '{ArtifactName}' must be copied to the test output directory.");
        var records = File.ReadAllLines(path, Encoding.UTF8).Select(ParseCsvLine).ToArray();
        records[0].ShouldBe(["path", "containing_type", "method", "parameter_types", "subject_type", "category", "disposition", "reason"]);
        return records.Skip(1).Where(fields => fields.Any(field => field.Length > 0)).Select(fields =>
        {
            fields.Length.ShouldBe(8, $"Each classification row must have eight fields: {string.Join(" | ", fields)}");
            return new Row(fields[0], fields[1], fields[2], fields[3], fields[4], fields[5], fields[6], fields[7]);
        }).ToArray();
    }

    private IReadOnlyList<Candidate> Scan(bool includeExtensions)
    {
        var sourceRoot = Repository.SourceRoot;
        var files = Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var trees = files.Select(path => CSharpSyntaxTree.ParseText(File.ReadAllText(path), new CSharpParseOptions(LanguageVersion.Latest), path)).ToArray();
        var interfaceNames = trees.SelectMany(tree => tree.GetRoot().DescendantNodes().OfType<InterfaceDeclarationSyntax>())
            .Select(node => node.Identifier.ValueText).ToHashSet(StringComparer.Ordinal);
        var references = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))?.Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path)).ToArray() ?? [];

        return trees.SelectMany(tree => ScanTree(tree, references, interfaceNames, includeExtensions, sourceRoot)).ToArray();
    }

    private static IReadOnlyList<Candidate> ScanText(string source, bool includeExtensions)
    {
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest), "probe.cs");
        var interfaces = tree.GetRoot().DescendantNodes().OfType<InterfaceDeclarationSyntax>()
            .Select(node => node.Identifier.ValueText).ToHashSet(StringComparer.Ordinal);
        var references = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))?.Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path)).ToArray() ?? [];
        return ScanTree(tree, references, interfaces, includeExtensions, string.Empty);
    }

    private static IReadOnlyList<Candidate> ScanTree(
        SyntaxTree tree,
        IReadOnlyList<MetadataReference> references,
        IReadOnlySet<string> interfaceNames,
        bool includeExtensions,
        string sourceRoot)
    {
        var compilation = CSharpCompilation.Create("StaticHelperClassification", [tree], references);
        var model = compilation.GetSemanticModel(tree, ignoreAccessibility: true);
        var results = new List<Candidate>();

        foreach (var method in tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (!method.Modifiers.Any(SyntaxKind.StaticKeyword) || IsPrivate(method))
                continue;
            if (method.ParameterList.Parameters.FirstOrDefault() is not { Type: { } type } parameter)
                continue;
            var isExtension = parameter.Modifiers.Any(SyntaxKind.ThisKeyword);
            if (isExtension != includeExtensions)
                continue;

            var symbol = model.GetTypeInfo(type).Type;
            if (symbol is not null && symbol.TypeKind == TypeKind.Interface)
                continue;
            if (symbol?.SpecialType is not (null or SpecialType.None))
                continue;
            if (!IsNamedTypeSyntax(type, interfaceNames))
                continue;
            var subjectType = type.WithoutTrivia().ToString();
            if (!IsTargetSubject(subjectType))
                continue;

            var containing = method.Ancestors().OfType<TypeDeclarationSyntax>().First();
            var parameterTypes = string.Join(";", method.ParameterList.Parameters.Select(item => item.Type?.WithoutTrivia().ToString() ?? "?"));
            var path = sourceRoot.Length == 0 ? tree.FilePath : Path.GetRelativePath(sourceRoot, tree.FilePath)
                .Replace(Path.DirectorySeparatorChar, '/');
            results.Add(new Candidate(path, containing.Identifier.ValueText, method.Identifier.ValueText,
                parameterTypes, subjectType, isExtension));
        }

        return results;
    }

    private static bool IsPrivate(MethodDeclarationSyntax method)
    {
        if (method.Modifiers.Any(SyntaxKind.PrivateKeyword))
            return true;
        if (method.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.PublicKeyword)
            || modifier.IsKind(SyntaxKind.InternalKeyword) || modifier.IsKind(SyntaxKind.ProtectedKeyword)))
            return false;
        return method.Parent is not InterfaceDeclarationSyntax;
    }

    private static bool IsTargetSubject(string subjectType) =>
        subjectType is "JsonObject" or "JsonElement" or "SqliteConnection" or "SqliteDataReader";

    private static bool IsNamedTypeSyntax(TypeSyntax type, IReadOnlySet<string> interfaceNames)
    {
        while (type is NullableTypeSyntax nullable)
            type = nullable.ElementType;
        if (type is not (IdentifierNameSyntax or GenericNameSyntax or QualifiedNameSyntax or AliasQualifiedNameSyntax))
            return false;
        var simpleName = type switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            GenericNameSyntax generic => generic.Identifier.ValueText,
            QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
            AliasQualifiedNameSyntax alias => alias.Name.Identifier.ValueText,
            _ => string.Empty
        };
        return !interfaceNames.Contains(simpleName);
    }

    private static string[] ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < line.Length; index++)
        {
            var ch = line[index];
            if (ch == '"')
            {
                if (quoted && index + 1 < line.Length && line[index + 1] == '"') { field.Append('"'); index++; }
                else quoted = !quoted;
            }
            else if (ch == ',' && !quoted) { fields.Add(field.ToString()); field.Clear(); }
            else field.Append(ch);
        }
        quoted.ShouldBeFalse($"Unterminated quoted CSV field: {line}");
        fields.Add(field.ToString());
        return fields.ToArray();
    }

    private sealed record Candidate(string Path, string ContainingType, string Method, string ParameterTypes, string SubjectType, bool IsExtension)
    {
        public string Key => $"{Path}|{ContainingType}|{Method}|{ParameterTypes}";
    }

    private sealed record Row(string Path, string ContainingType, string Method, string ParameterTypes, string SubjectType, string Category, string Disposition, string Reason)
    {
        public string Key => $"{Path}|{ContainingType}|{Method}|{ParameterTypes}";
    }
}
