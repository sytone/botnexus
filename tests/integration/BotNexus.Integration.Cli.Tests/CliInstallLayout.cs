using System.IO.Compression;
using System.Reflection;

namespace BotNexus.Integration.Cli.Tests;

/// <summary>
/// Pure inspection and reporting helpers for the layout produced by
/// <c>dotnet tool install --tool-path</c>.
///
/// Why this exists (issue #3237): the locally packed <c>botnexus</c> binary was observed to
/// start and then die at assembly-load time with
/// <c>Could not load file or assembly 'BotNexus.Agent.Providers.Core'</c>, non-deterministically.
/// The harness captured StdErr but never captured the install layout, so a single failing run
/// could not distinguish "the file is genuinely absent" from "the file is present and the bind
/// failed". Two gate runs (~20 minutes) were needed to learn nothing.
///
/// These functions are deliberately free of process invocation and filesystem side effects
/// beyond reading, so the guard itself can be tested with a synthesized layout — including the
/// negative case where the provider assembly is deliberately absent.
/// </summary>
internal static class CliInstallLayout
{
    /// <summary>
    /// Assemblies the packed CLI binds against during startup/<c>init</c>. Absence of any of
    /// these produces a process that starts and then fails at assembly-load time, which surfaces
    /// as an unrelated-looking test failure much later. Verified at pack/install time instead.
    ///
    /// Add to this list when the CLI takes a new startup-critical dependency.
    /// </summary>
    public static readonly IReadOnlyList<string> RequiredAssemblies =
    [
        "BotNexus.Cli.dll",
        "BotNexus.Gateway.dll",
        "BotNexus.Cron.dll",
        "BotNexus.Agent.Providers.Core.dll",
    ];

    /// <summary>Maximum number of listing entries rendered into a failure message.</summary>
    public const int MaxListedEntries = 500;

    /// <summary>
    /// Enumerates every file under <paramref name="installDir"/> as a sorted list of paths
    /// relative to that directory. Returns an empty list when the directory does not exist,
    /// which is itself diagnostic and must not throw while building a failure message.
    /// </summary>
    public static IReadOnlyList<string> EnumerateFiles(string installDir)
    {
        if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir))
            return [];

        var full = Path.GetFullPath(installDir);
        var results = new List<string>();
        foreach (var path in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
            results.Add(Path.GetRelativePath(full, path).Replace('\\', '/'));

        results.Sort(StringComparer.OrdinalIgnoreCase);
        return results;
    }

    /// <summary>
    /// Renders the resolved install directory and its file listing for inclusion in a failure
    /// message. Truncation is reported explicitly so a reader never mistakes a clipped listing
    /// for a short one.
    /// </summary>
    public static string Describe(string installDir, IReadOnlyList<string>? files = null, int maxEntries = MaxListedEntries)
    {
        files ??= EnumerateFiles(installDir);

        var header = $"Install directory: {installDir}" +
                     (Directory.Exists(installDir) ? string.Empty : "  (DOES NOT EXIST)");

        if (files.Count == 0)
            return header + Environment.NewLine + "Install layout: <no files>";

        var shown = files.Count <= maxEntries ? files : files.Take(maxEntries).ToList();
        var body = string.Join(Environment.NewLine, shown.Select(f => "  " + f));
        var suffix = files.Count > maxEntries
            ? $"{Environment.NewLine}  ... {files.Count - maxEntries} further entries omitted ({files.Count} total)"
            : $"{Environment.NewLine}  ({files.Count} entries)";

        return header + Environment.NewLine + "Install layout:" + Environment.NewLine + body + suffix;
    }

    /// <summary>
    /// Returns the required assemblies that do not appear anywhere in <paramref name="installedFiles"/>.
    /// Matching is on file name only, because the tool layout nests payload assemblies under
    /// <c>.store/&lt;package&gt;/&lt;version&gt;/tools/&lt;tfm&gt;/any/</c> and the exact nesting is a
    /// detail of the SDK, not a contract this harness should assert.
    /// </summary>
    public static IReadOnlyList<string> FindMissingAssemblies(
        IEnumerable<string> installedFiles,
        IEnumerable<string>? required = null)
    {
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in installedFiles)
        {
            var name = Path.GetFileName(file.Replace('\\', '/'));
            if (!string.IsNullOrEmpty(name))
                present.Add(name);
        }

        return (required ?? RequiredAssemblies)
            .Where(r => !present.Contains(r))
            .ToList();
    }

    /// <summary>
    /// Reads the managed assemblies carried in the <c>tools/</c> folder of the single
    /// <c>.nupkg</c> in <paramref name="packOutputDir"/>. Comparing this against the install
    /// layout distinguishes "the pack step omitted the assembly" from "the install step dropped
    /// it", which is the distinction the #3237 evidence could not make.
    /// Returns an empty list if no package is found or it cannot be read.
    /// </summary>
    public static IReadOnlyList<string> ReadPackagedToolAssemblies(string packOutputDir)
    {
        if (string.IsNullOrWhiteSpace(packOutputDir) || !Directory.Exists(packOutputDir))
            return [];

        var nupkg = Directory.EnumerateFiles(packOutputDir, "*.nupkg", SearchOption.TopDirectoryOnly)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (nupkg is null)
            return [];

        try
        {
            using var archive = ZipFile.OpenRead(nupkg);
            return archive.Entries
                .Select(e => e.FullName.Replace('\\', '/'))
                .Where(n => n.StartsWith("tools/", StringComparison.OrdinalIgnoreCase)
                            && n.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                .Select(n => n[(n.LastIndexOf('/') + 1)..])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception)
        {
            // A malformed or locked package is reported by the caller's missing-assembly check;
            // failing to read it must not mask that message with an unrelated exception.
            return [];
        }
    }

    /// <summary>
    /// Returns required assemblies that are present in <paramref name="installDir"/> but whose
    /// <see cref="AssemblyName.Version"/> does not match <paramref name="expectedVersion"/>.
    ///
    /// Presence alone is NOT sufficient (issue #3237). The observed failure had
    /// <c>BotNexus.Agent.Providers.Core.dll</c> sitting in the install layout while the CLI still
    /// died with <c>Could not load file or assembly ... Version=99.99.99.0</c>: the packed CLI was
    /// compiled against the pack-time version stamp, but a stale build of the dependency carrying a
    /// different assembly version was packaged alongside it. That binds to nothing and fails at
    /// startup. Checking the version here turns that into a pack/install-step failure that names
    /// the assembly and both versions.
    /// </summary>
    public static IReadOnlyList<string> FindVersionMismatches(
        string installDir,
        Version expectedVersion,
        IEnumerable<string>? required = null)
    {
        if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir))
            return [];

        var wanted = new HashSet<string>(required ?? RequiredAssemblies, StringComparer.OrdinalIgnoreCase);
        var mismatches = new List<string>();

        foreach (var path in Directory.EnumerateFiles(installDir, "*.dll", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(path);
            if (!wanted.Contains(name))
                continue;

            Version? actual;
            try
            {
                actual = AssemblyName.GetAssemblyName(path).Version;
            }
            catch (Exception ex)
            {
                mismatches.Add($"{name} (could not read assembly identity at {path}: {ex.GetType().Name})");
                continue;
            }

            if (actual != expectedVersion)
                mismatches.Add($"{name} (expected {expectedVersion}, found {actual?.ToString() ?? "<none>"}) at {path}");
        }

        mismatches.Sort(StringComparer.OrdinalIgnoreCase);
        return mismatches;
    }

    /// <summary>
    /// Converts a pack version stamp such as <c>99.99.99-local-deadbeef</c> into the four-part
    /// assembly version the CLI will bind against (<c>99.99.99.0</c>). Pre-release suffixes do not
    /// appear in an assembly version.
    /// </summary>
    public static Version ToAssemblyVersion(string packVersion)
    {
        var core = packVersion.Split('-', 2)[0];
        var parts = core.Split('.');
        int Part(int i) => parts.Length > i && int.TryParse(parts[i], out var v) ? v : 0;
        return new Version(Part(0), Part(1), Part(2), Part(3));
    }

    /// <summary>
    /// Builds the pack/install-step failure message for assemblies that are present but bind to the
    /// wrong version, carrying the install directory and listing for the same reason as
    /// <see cref="FormatMissingAssemblyFailure"/>.
    /// </summary>
    public static string FormatVersionMismatchFailure(
        string installDir,
        Version expectedVersion,
        IReadOnlyList<string> mismatches,
        IReadOnlyList<string>? installedFiles = null)
    {
        return string.Join(Environment.NewLine + Environment.NewLine,
            $"Local CLI pack/install produced an install layout whose assemblies do not bind. " +
            $"Expected assembly version {expectedVersion}; mismatched: {string.Join(", ", mismatches)}.",
            "The packed binary would start and then fail at assembly-load time with " +
            "'Could not load file or assembly', so this step fails here by name instead (see issue #3237).",
            Describe(installDir, installedFiles));
    }

    /// <summary>
    /// Builds the pack/install-step failure message required by #3237 AC1: it names every missing
    /// assembly explicitly, and carries the resolved install directory plus its listing so the
    /// first failing run is sufficient to diagnose.
    /// </summary>
    public static string FormatMissingAssemblyFailure(
        string installDir,
        IReadOnlyList<string> missing,
        IReadOnlyList<string>? installedFiles = null,
        IReadOnlyList<string>? packagedAssemblies = null)
    {
        installedFiles ??= EnumerateFiles(installDir);

        var lines = new List<string>
        {
            "Local CLI pack/install produced an incomplete install layout. " +
            "Missing assembly/assemblies: " + string.Join(", ", missing) + ".",
            "The packed binary would start and then fail at assembly-load time, so this step " +
            "fails here by name rather than letting a later test fail on a symptom (see issue #3237).",
            Describe(installDir, installedFiles),
        };

        if (packagedAssemblies is { Count: > 0 })
        {
            var missingFromPackage = missing
                .Where(m => !packagedAssemblies.Contains(m, StringComparer.OrdinalIgnoreCase))
                .ToList();

            lines.Add(missingFromPackage.Count == 0
                ? "Package tools/ payload DOES contain the missing assembly/assemblies — the install step dropped it."
                : "Package tools/ payload is ALSO missing: " + string.Join(", ", missingFromPackage) + " — the pack step omitted it.");
            lines.Add("Packaged tools/ assemblies:" + Environment.NewLine +
                      string.Join(Environment.NewLine, packagedAssemblies.Select(a => "  " + a)));
        }

        return string.Join(Environment.NewLine + Environment.NewLine, lines);
    }

    /// <summary>
    /// Builds the diagnostic message required by #3237 AC2 for a CLI invocation that exited
    /// non-zero: stdout, stderr, the resolved install directory and its file listing together,
    /// so one failing run distinguishes an absent file from a binding failure.
    /// </summary>
    public static string FormatCliFailure(
        string commandDescription,
        int? exitCode,
        string stdOut,
        string stdErr,
        string installDir,
        IReadOnlyList<string>? installedFiles = null)
    {
        var exit = exitCode is { } code ? code.ToString() : "<still running>";
        return string.Join(Environment.NewLine,
            $"CLI invocation failed: {commandDescription}",
            $"ExitCode: {exit}",
            "StdOut:",
            stdOut,
            "StdErr:",
            stdErr,
            Describe(installDir, installedFiles));
    }
}
