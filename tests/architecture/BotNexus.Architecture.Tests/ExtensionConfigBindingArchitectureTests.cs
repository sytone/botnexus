using System.Text.RegularExpressions;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Fences extension configuration binding (#3492): no extension deserialises an entry from
/// <c>AgentDescriptor.ExtensionConfig</c> with default, case-sensitive options.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a fence and not a code review.</b> The configuration file is camelCase; extension config
/// POCOs are PascalCase. <c>JsonSerializer</c>'s default options are case-SENSITIVE, so
/// deserialising a raw element without options binds nothing - every property silently takes its
/// C# default and no error reaches the operator or the log.
/// </para>
/// <para>
/// That is invisible wherever the configured value happens to equal the default, which is how it
/// survived across eight contributors: only <c>allowSharedSkillManagement</c>, whose default is
/// <see langword="false"/>, was ever noticed. Eleven call sites had drifted into three different
/// behaviours, and the three that were correct were correct by accident of using
/// <c>JsonSerializerDefaults.Web</c> for an unrelated reason.
/// </para>
/// <para>
/// A code review cannot catch the ninth. The failure produces working software with wrong values,
/// so there is no crash, no log line, and no test failure to notice - which is precisely the
/// signature of a defect that needs a build-time fence rather than vigilance.
/// </para>
/// </remarks>
public sealed class ExtensionConfigBindingArchitectureTests : ArchitectureTest
{
    /// <summary>
    /// The binder itself is the sanctioned seam and necessarily contains the only raw
    /// <c>Deserialize</c> call in this space.
    /// </summary>
    private static readonly string[] AllowedFiles =
    [
        "ExtensionConfigBinder.cs",
    ];

    /// <summary>
    /// Matches retrieval of an entry from the extension config bag, which is the marker that a file
    /// participates in extension configuration binding at all.
    /// </summary>
    private static readonly Regex ExtensionConfigRead =
        new(@"ExtensionConfig(uration)?\s*\??\s*\.\s*TryGetValue\s*\(", RegexOptions.Compiled);

    /// <summary>
    /// Matches a <c>Deserialize</c> call whose argument list ends immediately after the raw text -
    /// that is, one that passes no <c>JsonSerializerOptions</c>.
    /// </summary>
    /// <remarks>
    /// Deliberately shaped around <c>GetRawText()</c> rather than any <c>Deserialize</c> call: the
    /// defect is specific to binding a <see cref="System.Text.Json.JsonElement"/> pulled from the
    /// config bag, and a broader pattern would fire on unrelated wire deserialisation that has its
    /// own, correct, options.
    /// </remarks>
    private static readonly Regex OptionlessRawTextDeserialize =
        new(@"Deserialize\s*<[^>]+>\s*\(\s*[A-Za-z_][A-Za-z0-9_]*\s*\.\s*GetRawText\s*\(\s*\)\s*\)",
            RegexOptions.Compiled);

    private IEnumerable<string> ProductionSourceFiles()
    {
        var srcRoot = Repository.SourceRoot;
        if (!Directory.Exists(srcRoot))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(Repository.Root, file).Replace('\\', '/');
            if (relative.Contains("/obj/", StringComparison.Ordinal) ||
                relative.Contains("/bin/", StringComparison.Ordinal))
            {
                continue;
            }

            yield return file;
        }
    }

    /// <summary>
    /// No production file binds an extension config element with default options.
    /// </summary>
    [Fact]
    public void ExtensionConfig_IsNeverDeserialisedWithDefaultOptions()
    {
        var offenders = new List<string>();

        foreach (var file in ProductionSourceFiles())
        {
            var name = Path.GetFileName(file);
            if (AllowedFiles.Contains(name, StringComparer.Ordinal))
            {
                continue;
            }

            var text = File.ReadAllText(file);

            // Scope to files that actually read the extension config bag. Provider message
            // converters deserialise raw JsonElements too, with their own correct wire options;
            // firing on those would be a false positive that teaches contributors to suppress
            // the fence rather than fix the defect.
            if (!ExtensionConfigRead.IsMatch(text))
            {
                continue;
            }

            if (!OptionlessRawTextDeserialize.IsMatch(text))
            {
                continue;
            }

            offenders.Add(Path.GetRelativePath(Repository.Root, file).Replace('\\', '/'));
        }

        offenders.ShouldBeEmpty(
            "Extension configuration must bind through ExtensionConfigBinder, which supplies " +
            "case-insensitive options. A bare Deserialize<T>(element.GetRawText()) binds nothing " +
            "when the file is camelCase and the POCO is PascalCase, and fails silently (#3492). " +
            "Offending files: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// Every file that reads the extension config bag routes binding through the shared seam.
    /// </summary>
    /// <remarks>
    /// The companion to the negative rule above. Without this, a contributor could reintroduce the
    /// defect by hand-rolling a flattener or by binding through some third mechanism that the
    /// optionless-deserialize pattern does not match; requiring the seam by name closes that.
    /// </remarks>
    [Fact]
    public void ExtensionConfig_ReadersBindThroughTheSharedSeam()
    {
        var offenders = new List<string>();

        foreach (var file in ProductionSourceFiles())
        {
            var name = Path.GetFileName(file);
            if (AllowedFiles.Contains(name, StringComparer.Ordinal))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            if (!ExtensionConfigRead.IsMatch(text))
            {
                continue;
            }

            if (text.Contains("ExtensionConfigBinder", StringComparison.Ordinal))
            {
                continue;
            }

            offenders.Add(Path.GetRelativePath(Repository.Root, file).Replace('\\', '/'));
        }

        offenders.ShouldBeEmpty(
            "A file that reads AgentDescriptor.ExtensionConfig must bind it through " +
            "ExtensionConfigBinder so camelCase configuration reaches PascalCase properties " +
            "(#3492). Offending files: " + string.Join(", ", offenders));
    }
}
