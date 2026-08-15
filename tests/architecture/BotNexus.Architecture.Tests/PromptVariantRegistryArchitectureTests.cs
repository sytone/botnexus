using System.Text.RegularExpressions;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Fitness fence for the startup-frozen prompt-variant registry (#2433).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a source-level fence.</b> #2433 states plainly that reflection must happen ONCE at
/// startup and never on the prompt-build path, because prompt assembly runs on every turn of every
/// agent. The behaviour suite proves the CURRENT code obeys that with a scan counter; it cannot
/// prevent the next author from reaching for <c>GetCustomAttributes</c> inside a section builder,
/// where the counter would move but no existing assertion would notice until someone profiled a
/// slow gateway.
/// </para>
/// <para>
/// <b>And a fence against the shape that was removed.</b> The defect #2433 fixes was a
/// <c>switch</c> over <c>ModelFamilyDetector</c> constants with <c>_ =&gt; []</c> as the fallback:
/// an unrecognised family silently received zero guidance. Re-introducing a family switch inside a
/// prompt section would restore that fail-open one family at a time, so it fails here instead.
/// </para>
/// </remarks>
public class PromptVariantRegistryArchitectureTests
{
    /// <summary>The one file permitted to reflect over prompt-variant declarations.</summary>
    private const string SanctionedRegistry =
        "src/gateway/BotNexus.Gateway.Prompts/Variants/PromptVariantRegistry.cs";

    /// <summary>Reflection APIs that must not appear anywhere else in the prompts assembly.</summary>
    private static readonly Regex ReflectionUse = new(
        @"\b(GetCustomAttributes?|GetTypes|GetMethods|GetProperties|Assembly\s*\.\s*Load)\b",
        RegexOptions.Compiled);

    /// <summary>
    /// A family switch: a <c>switch</c> whose arms name <see cref="ModelFamilyDetector"/> constants.
    /// This is the exact shape #2433 deleted.
    /// </summary>
    private static readonly Regex FamilySwitch = new(
        @"ModelFamilyDetector\s*\.\s*(Claude|Gpt|Gemini|Copilot|DeepSeek|Qwen|Llama)\s*=>",
        RegexOptions.Compiled);

    private static string RepoRoot => FindRepoRoot();

    /// <summary>
    /// AC3: no reflection on the prompt-build path. Only the registry reflects, and it does so from
    /// <c>Freeze</c>.
    /// </summary>
    [Fact]
    public void OnlyTheVariantRegistry_ReflectsInsideThePromptsAssembly()
    {
        var offenders = new List<string>();

        foreach (var file in EnumeratePromptSources())
        {
            var relative = ToRepoRelative(file);
            if (string.Equals(relative, SanctionedRegistry, StringComparison.OrdinalIgnoreCase))
                continue;

            if (ReflectionUse.IsMatch(StripComments(File.ReadAllText(file))))
                offenders.Add(relative);
        }

        offenders.ShouldBeEmpty(
            "#2433 requires prompt-variant discovery to happen ONCE at startup, inside " +
            $"{SanctionedRegistry}, and never on the prompt-build path -- prompt assembly runs on " +
            "every turn of every agent, so a type scan there is a per-turn cost paid forever. " +
            "Declare the variant with [PromptVariant] and let the frozen registry resolve it. " +
            "Offenders: " + string.Join("; ", offenders));
    }

    /// <summary>
    /// AC1/AC2: the hardcoded family switch is gone and does not come back. A switch arm per family
    /// is how the fail-open was spelled: every family the switch had not heard of got nothing.
    /// </summary>
    [Fact]
    public void NoPromptSection_SwitchesOnModelFamily()
    {
        var offenders = EnumeratePromptSources()
            .Where(file => FamilySwitch.IsMatch(StripComments(File.ReadAllText(file))))
            .Select(ToRepoRelative)
            .ToList();

        offenders.ShouldBeEmpty(
            "Per-family prompt content is DECLARED with [PromptVariant(sectionId, Family = ...)] " +
            "and resolved through the frozen ladder (#2433). A switch over ModelFamilyDetector " +
            "constants is the shape that was removed: it emitted nothing at all for any family it " +
            "had never heard of. Offenders: " + string.Join("; ", offenders));
    }

    /// <summary>
    /// The registry must consume <c>ModelFamilyVersion</c> rather than growing a second version
    /// parser -- the standing constraint from #2374, restated as an explicit clause of #2433.
    /// </summary>
    [Fact]
    public void VariantRegistry_ParsesVersionsThroughModelFamilyVersion()
    {
        var source = File.ReadAllText(ResolvePath(SanctionedRegistry));

        source.Contains("ModelFamilyVersion.TryParse", StringComparison.Ordinal).ShouldBeTrue(
            "#2433 requires version matching to reuse ModelFamilyVersion (#2374). A second parser " +
            "is how 'claude-opus-4.50' sorted below 'claude-opus-4.6' the first time.");
    }

    /// <summary>
    /// Vacuity guard: a fence that enumerated nothing would pass for the wrong reason, and the
    /// sanctioned file must actually exist or the exemption above is silently exempting nothing.
    /// </summary>
    [Fact]
    public void Fence_IsNotVacuous()
    {
        EnumeratePromptSources().Count.ShouldBeGreaterThan(10,
            "expected to enumerate the prompts assembly's sources; enumeration is broken");

        File.Exists(ResolvePath(SanctionedRegistry)).ShouldBeTrue(
            $"{SanctionedRegistry} must exist; if the registry moved, update this fence deliberately.");

        // The detectors must fire on the shapes they claim to detect.
        ReflectionUse.IsMatch("member.GetCustomAttributes<PromptVariantAttribute>()").ShouldBeTrue();
        FamilySwitch.IsMatch("ModelFamilyDetector.Claude => ClaudeGuidance,").ShouldBeTrue();
        FamilySwitch.IsMatch("var family = ModelFamilyDetector.Claude;").ShouldBeFalse();
    }

    // ---- helpers ----

    private static List<string> EnumeratePromptSources() =>
        Directory
            .EnumerateFiles(
                Path.Combine(RepoRoot, "src", "gateway", "BotNexus.Gateway.Prompts"),
                "*.cs",
                SearchOption.AllDirectories)
            .Where(static path =>
            {
                var normalized = path.Replace('\\', '/');
                return !normalized.Contains("/bin/") && !normalized.Contains("/obj/");
            })
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Strips comments so a doc comment that DESCRIBES the removed switch (as several deliberately
    /// do, to explain why it went) is not mistaken for the switch itself.
    /// </summary>
    private static string StripComments(string source)
    {
        source = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(source, @"^\s*//.*$", string.Empty, RegexOptions.Multiline);
    }

    private static string ResolvePath(string relative) =>
        Path.Combine(RepoRoot, relative.Replace('/', Path.DirectorySeparatorChar));

    private static string ToRepoRelative(string absolute) =>
        Path.GetRelativePath(RepoRoot, absolute).Replace('\\', '/');

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "BotNexus.slnx")))
        {
            current = current.Parent;
        }

        current.ShouldNotBeNull("Could not locate repo root (BotNexus.slnx) from " + AppContext.BaseDirectory);
        return current!.FullName;
    }
}
