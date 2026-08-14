using System.Reflection;
using System.Text.RegularExpressions;
using BotNexus.Gateway.Abstractions.Configuration;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// The analyser behind the #2888 config-path fence: it enumerates config-path string literals that
/// are passed to the configuration access surface from source files OUTSIDE
/// <c>BotNexus.Gateway.Configuration</c>, and decides for each whether
/// <see cref="IConfigPathResolver"/> can resolve it against the typed <see cref="PlatformConfig"/>
/// graph.
///
/// <para>
/// <b>Why a separate class.</b> This carries no xunit or Shouldly dependency so the exact same
/// extraction and resolution used by the fence can be driven from a throwaway harness when the
/// baseline needs regenerating. A baseline produced by a second, hand-written implementation would
/// be a baseline for a different rule.
/// </para>
///
/// <para>
/// <b>Why the access surface is narrow (deliberately).</b> The issue calls out over-matching as the
/// main risk: a fence that flags every string that merely looks path-ish creates friction and
/// generates pressure to weaken it. So the extraction is anchored to the three ways a consumer
/// actually names a config path:
/// <list type="number">
/// <item>a raw-document root indexer - <c>root["compaction"]</c>, the literal #2764 shape;</item>
/// <item>a dotted path handed to the resolver or to <c>BoundConfigPath</c>; and</item>
/// <item>a <c>*Path</c> string constant holding a dotted path, which is how the fixed #2764 call
/// sites name their target (<c>SummarizationModelPath</c>) - catching only the inline literal would
/// miss the very shape the repository now uses.</item>
/// </list>
/// A string that is not one of those three is not evidence that anyone reads configuration there,
/// so it is not this fence's business.
/// </para>
/// </summary>
internal static class ConfigPathFence
{
    /// <summary>Project directory (relative to the repo root) that owns configuration itself.</summary>
    private const string ConfigurationProjectDirectory = "src/gateway/BotNexus.Gateway.Configuration/";

    /// <summary>
    /// A raw <c>JsonObject</c> document root indexed by a literal key. This is exactly the #2764
    /// defect shape. Anchored to identifiers that name the document root so that indexing an
    /// arbitrary dictionary elsewhere is not swept in.
    /// </summary>
    private static readonly Regex RootIndexer = new(
        @"\b(?:root|configRoot|rootObject|document)\s*\??\[\s*""(?<path>[^""]+)""\s*\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// A path literal in the second argument position of the config access surface -
    /// <see cref="IConfigPathResolver.TryGetValue"/>, <see cref="IConfigPathResolver.TrySetValue"/>
    /// and the two <c>BoundConfigPath</c> members that wrap it.
    /// </summary>
    private static readonly Regex AccessSurfaceArgument = new(
        @"\b(?:TryGetValue|TrySetValue|TryReadString|WriteString)\s*\(\s*[A-Za-z_][A-Za-z0-9_.]*\s*,\s*""(?<path>[^""]+)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// A <c>*Path</c> string constant whose value is a dotted config path. Consumers that were fixed
    /// for #2764 name their target this way, so an inline-literal-only fence would be blind to the
    /// shape the codebase actually uses today.
    /// </summary>
    private static readonly Regex ConfigPathConstant = new(
        @"\bconst\s+string\s+(?<name>[A-Za-z0-9_]*Path)\s*=\s*""(?<path>[^""]+)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Dotted, slash-free, whitespace-free identifier chain. Anything else in a <c>*Path</c>
    /// constant is a filesystem or URL path, not a config path.
    /// </summary>
    private static readonly Regex DottedConfigPathShape = new(
        @"^[A-Za-z_][A-Za-z0-9_]*(?:\[[0-9]+\])?(?:\.[A-Za-z_][A-Za-z0-9_]*(?:\[[0-9]+\])?)+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Trailing segments that make a dotted string a filename rather than a config path. Without
    /// this, <c>"appsettings.json"</c> in a <c>*Path</c> constant would be reported as an
    /// unresolvable config path, which is the over-matching the issue warns against.
    /// </summary>
    private static readonly HashSet<string> FileExtensionSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "json", "md", "txt", "log", "db", "sqlite", "cs", "razor", "exe", "dll",
        "yml", "yaml", "ps1", "sh", "xml", "config", "toml", "ini", "png", "js", "css"
    };

    /// <summary>One config-path literal, and where it was written.</summary>
    internal sealed record Usage(string RelativeFile, string Path)
    {
        /// <summary>Stable baseline key: file and literal, so two literals in one file are distinct entries.</summary>
        public string Key => $"{RelativeFile}|{Path}";
    }

    /// <summary>A <see cref="Usage"/> the resolver cannot resolve, with the closest path that it can.</summary>
    internal sealed record Violation(Usage Usage, string ResolverError, string Suggestion)
    {
        public string Key => Usage.Key;

        public string Describe()
            => $"{Usage.RelativeFile}: config path \"{Usage.Path}\" does not resolve " +
               $"({ResolverError.TrimEnd('.')}). Closest resolvable path: \"{Suggestion}\".";
    }

    /// <summary>
    /// Extracts every config-path literal used by source outside the configuration project.
    /// </summary>
    /// <param name="repoRoot">Repository root (the directory holding <c>BotNexus.slnx</c>).</param>
    internal static IReadOnlyList<Usage> ExtractUsages(string repoRoot)
    {
        var usages = new List<Usage>();
        var srcRoot = Path.Combine(repoRoot, "src");
        if (!Directory.Exists(srcRoot))
            return usages;

        foreach (var file in Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');

            // Build output is not source.
            if (relative.Contains("/obj/", StringComparison.Ordinal) ||
                relative.Contains("/bin/", StringComparison.Ordinal))
                continue;

            // The configuration project defines the paths; it cannot violate its own contract.
            if (relative.StartsWith(ConfigurationProjectDirectory, StringComparison.OrdinalIgnoreCase))
                continue;

            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var path in ExtractFromText(text))
                usages.Add(new Usage(relative, path));
        }

        return usages
            .DistinctBy(u => u.Key, StringComparer.Ordinal)
            .OrderBy(u => u.Key, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The pure text half of the extraction, exposed so the fence can pin the predicate against
    /// synthetic source without touching the working tree.
    /// </summary>
    internal static IReadOnlyList<string> ExtractFromText(string text)
    {
        // Comments are not reads. The corrected #2764 call site carries the literal
        // root["compaction"] inside a comment explaining what NOT to do, and flagging that would
        // make the fence punish the very documentation of its own rule.
        text = StripComments(text);

        var paths = new List<string>();

        foreach (Match match in RootIndexer.Matches(text))
        {
            var group = match.Groups["path"];
            if (group.Success)
                paths.Add(group.Value);
        }

        foreach (Match match in AccessSurfaceArgument.Matches(text))
            paths.Add(match.Groups["path"].Value);

        foreach (Match match in ConfigPathConstant.Matches(text))
        {
            var value = match.Groups["path"].Value;
            if (LooksLikeConfigPath(value))
                paths.Add(value);
        }

        return paths
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Removes <c>//</c> and <c>/* */</c> comments, leaving string literals intact. Deliberately
    /// simple: it tracks whether it is inside a string or char literal so that a <c>"//"</c> inside
    /// a path literal is not mistaken for the start of a comment.
    /// </summary>
    private static string StripComments(string text)
    {
        var output = new System.Text.StringBuilder(text.Length);
        var i = 0;

        while (i < text.Length)
        {
            var c = text[i];

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n')
                    i++;
                continue;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < text.Length && !(text[i] == '*' && text[i + 1] == '/'))
                    i++;
                i = Math.Min(i + 2, text.Length);
                continue;
            }

            if (c is '"' or '\'')
            {
                var quote = c;
                output.Append(c);
                i++;
                while (i < text.Length && text[i] != quote)
                {
                    if (text[i] == '\\' && i + 1 < text.Length)
                    {
                        output.Append(text[i]);
                        i++;
                    }

                    output.Append(text[i]);
                    i++;
                }

                if (i < text.Length)
                {
                    output.Append(text[i]);
                    i++;
                }

                continue;
            }

            output.Append(c);
            i++;
        }

        return output.ToString();
    }

    private static bool LooksLikeConfigPath(string value)
    {
        if (!DottedConfigPathShape.IsMatch(value))
            return false;

        var lastSegment = value[(value.LastIndexOf('.') + 1)..];
        return !FileExtensionSegments.Contains(lastSegment);
    }

    /// <summary>
    /// Resolves every extracted literal through <see cref="IConfigPathResolver"/> and returns the
    /// ones it cannot bind, each with the nearest path that does bind.
    /// </summary>
    internal static IReadOnlyList<Violation> FindViolations(IEnumerable<Usage> usages)
    {
        var resolver = new ConfigPathResolver();
        var candidates = KnownPaths.Value;
        var violations = new List<Violation>();

        foreach (var usage in usages)
        {
            if (IsBound(resolver, usage.Path, out var error))
                continue;

            violations.Add(new Violation(usage, error, NearestPath(usage.Path, candidates)));
        }

        return violations
            .OrderBy(v => v.Key, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Asks the resolver whether the path exists, using the same probe technique
    /// <c>BoundConfigPath.IsBound</c> uses in production: attempt the write against a throwaway
    /// graph and keep only the yes/no answer.
    /// </summary>
    private static bool IsBound(IConfigPathResolver resolver, string path, out string error)
    {
        if (resolver.TrySetValue(new PlatformConfig(), path, null, out error))
            return true;

        // The probe writes null purely to force the walk; a non-nullable leaf rejecting null proves
        // the path WAS resolved all the way down. Treating that as unresolvable would flag correct
        // consumers of every value-typed setting.
        if (error.Contains("does not allow null", StringComparison.Ordinal))
        {
            error = string.Empty;
            return true;
        }

        return false;
    }

    // ── Suggestion ────────────────────────────────────────────────────────────

    /// <summary>
    /// Every dotted path the typed graph binds, derived by reflection over the
    /// <see cref="PlatformConfig"/> TYPE rather than over an instance. An instance walk would only
    /// see paths whose intermediate nodes happen to be non-null, which on a fresh config is almost
    /// none - the suggestion would then be useless exactly when it is needed.
    /// </summary>
    private static readonly Lazy<IReadOnlyList<string>> KnownPaths =
        new(() =>
        {
            var paths = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            WalkType(typeof(PlatformConfig), string.Empty, paths, new HashSet<Type>(), depth: 0);
            return paths.ToList();
        });

    private const int MaxSuggestionDepth = 4;

    private static void WalkType(Type type, string prefix, ISet<string> paths, HashSet<Type> ancestry, int depth)
    {
        if (depth > MaxSuggestionDepth || !ancestry.Add(type))
            return;

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0)
                continue;

            var name = ToCamelCase(property.Name);
            var path = prefix.Length == 0 ? name : $"{prefix}.{name}";
            paths.Add(path);

            var propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            if (IsConfigPoco(propertyType))
                WalkType(propertyType, path, paths, ancestry, depth + 1);
        }

        ancestry.Remove(type);
    }

    private static bool IsConfigPoco(Type type)
        => type.IsClass &&
           type != typeof(string) &&
           !type.IsArray &&
           !type.IsGenericType &&
           type.Namespace is { } ns &&
           ns.StartsWith("BotNexus", StringComparison.Ordinal);

    private static string ToCamelCase(string value)
        => value.Length switch
        {
            0 => value,
            1 => value.ToLowerInvariant(),
            _ => char.ToLowerInvariant(value[0]) + value[1..]
        };

    /// <summary>
    /// The closest resolvable path. A pure edit-distance nearest neighbour is a bad suggester here:
    /// for the #2764 literal <c>compaction</c> it returns <c>cron</c> (distance 7) over the correct
    /// <c>gateway.compaction</c> (distance 8), because the right answer is a PREFIXED form of the
    /// wrong one and prefixing costs edits. So a candidate whose trailing segments match the
    /// literal's wins outright - that is the actual defect shape, a consumer reading at the wrong
    /// depth - and edit distance is only the tie-break.
    /// </summary>
    internal static string NearestPath(string path, IReadOnlyList<string> candidates)
    {
        var best = "(no resolvable path found)";
        var bestScore = (SuffixSegments: -1, Distance: int.MaxValue, Length: int.MaxValue);

        foreach (var candidate in candidates)
        {
            var score = (
                SuffixSegments: MatchingTrailingSegments(path, candidate),
                Distance: EditDistance(path, candidate),
                Length: candidate.Length);

            var better = score.SuffixSegments > bestScore.SuffixSegments
                || (score.SuffixSegments == bestScore.SuffixSegments
                    && (score.Distance < bestScore.Distance
                        || (score.Distance == bestScore.Distance && score.Length < bestScore.Length)));

            if (!better)
                continue;

            bestScore = score;
            best = candidate;
        }

        return best;
    }

    /// <summary>Number of trailing dotted segments the two paths share, case-insensitively.</summary>
    private static int MatchingTrailingSegments(string path, string candidate)
    {
        var a = path.Split('.');
        var b = candidate.Split('.');
        var matched = 0;

        while (matched < a.Length && matched < b.Length &&
               string.Equals(a[^(matched + 1)], b[^(matched + 1)], StringComparison.OrdinalIgnoreCase))
        {
            matched++;
        }

        return matched;
    }

    private static int EditDistance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];

        for (var j = 0; j <= b.Length; j++)
            previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    /// <summary>Locates the repository root by walking up to the solution file.</summary>
    internal static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "BotNexus.slnx")))
            current = current.Parent;

        return current?.FullName
               ?? throw new DirectoryNotFoundException(
                   "Could not locate the repository root from " + AppContext.BaseDirectory);
    }
}
