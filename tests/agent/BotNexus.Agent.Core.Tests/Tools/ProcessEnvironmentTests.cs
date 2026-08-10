using BotNexus.Agent.Core.Tools;

namespace BotNexus.Agent.Core.Tests.Tools;

/// <summary>
/// Coverage for issue #2892. Child-process environment overrides were written straight into
/// <c>ProcessStartInfo.Environment</c>, so the CALLER dictionary's comparer decided collision
/// behaviour. On Windows - where the real environment block is case-insensitive - an override
/// spelled <c>path</c> over an inherited <c>PATH</c> produced two entries for one logical
/// variable or was silently dropped. These tests pin the platform-correct rule at the shared
/// seam, exercising BOTH platform branches on whatever machine they run on by passing the
/// comparer explicitly.
/// </summary>
public class ProcessEnvironmentTests
{
    /// <summary>AC2 - Windows rule: one entry survives and it carries the override's value.</summary>
    [Fact]
    public void Merge_WindowsRule_DifferentlyCasedOverride_ReplacesInheritedEntry()
    {
        var target = new Dictionary<string, string?>(StringComparer.Ordinal) { ["PATH"] = "Y" };

        ProcessEnvironment.Merge(
            target,
            new Dictionary<string, string> { ["path"] = "X" },
            StringComparer.OrdinalIgnoreCase);

        target.Count.ShouldBe(1);
        target.Keys.Single().ShouldBe("path");
        target.Values.Single().ShouldBe("X");
    }

    /// <summary>AC3 - POSIX rule: the two spellings are genuinely different variables.</summary>
    [Fact]
    public void Merge_PosixRule_DifferentlyCasedOverride_KeepsBothEntries()
    {
        var target = new Dictionary<string, string?>(StringComparer.Ordinal) { ["PATH"] = "Y" };

        ProcessEnvironment.Merge(
            target,
            new Dictionary<string, string> { ["path"] = "X" },
            StringComparer.Ordinal);

        target.Count.ShouldBe(2);
        target["PATH"].ShouldBe("Y");
        target["path"].ShouldBe("X");
    }

    /// <summary>
    /// An exactly-matching key must still be a plain overwrite on both platforms - the
    /// collision-removal step must not delete the entry it is about to write.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Merge_ExactKeyMatch_OverwritesInPlace(bool windowsRule)
    {
        var target = new Dictionary<string, string?>(StringComparer.Ordinal) { ["HOME"] = "old" };

        ProcessEnvironment.Merge(
            target,
            new Dictionary<string, string> { ["HOME"] = "new" },
            windowsRule ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        target.Count.ShouldBe(1);
        target["HOME"].ShouldBe("new");
    }

    /// <summary>
    /// Variables the caller did not mention are inherited untouched - the merge must not be a
    /// wholesale replacement of the parent block.
    /// </summary>
    [Fact]
    public void Merge_LeavesUnrelatedInheritedVariablesIntact()
    {
        var target = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["PATH"] = "Y",
            ["UNRELATED"] = "keep",
        };

        ProcessEnvironment.Merge(
            target,
            new Dictionary<string, string> { ["path"] = "X" },
            StringComparer.OrdinalIgnoreCase);

        target["UNRELATED"].ShouldBe("keep");
    }

    /// <summary>
    /// The default comparer must follow the host, not a hardcoded choice - that platform
    /// coupling is the entire point of the helper.
    /// </summary>
    [Fact]
    public void KeyComparer_MatchesHostPlatformSemantics()
    {
        ProcessEnvironment.KeyComparer.ShouldBe(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    }

    /// <summary>
    /// Called with no explicit comparer the helper applies the host rule, so production call
    /// sites that omit the argument are still platform-correct.
    /// </summary>
    [Fact]
    public void Merge_WithoutExplicitComparer_AppliesHostPlatformRule()
    {
        var target = new Dictionary<string, string?>(StringComparer.Ordinal) { ["SAMPLE_VAR"] = "Y" };

        ProcessEnvironment.Merge(target, new Dictionary<string, string> { ["sample_var"] = "X" });

        if (OperatingSystem.IsWindows())
        {
            target.Count.ShouldBe(1);
            target["sample_var"].ShouldBe("X");
        }
        else
        {
            target.Count.ShouldBe(2);
            target["SAMPLE_VAR"].ShouldBe("Y");
            target["sample_var"].ShouldBe("X");
        }
    }

    /// <summary>
    /// The value projection seam exists so a call site with its own placeholder syntax does not
    /// need a second merge loop of its own - the defect #2892 fixed was exactly such duplication.
    /// </summary>
    [Fact]
    public void Merge_AppliesValueTransformBeforeWriting()
    {
        var target = new Dictionary<string, string?>(StringComparer.Ordinal);

        ProcessEnvironment.Merge(
            target,
            new Dictionary<string, string> { ["TOKEN"] = "raw" },
            StringComparer.Ordinal,
            value => value + "-resolved");

        target["TOKEN"].ShouldBe("raw-resolved");
    }
}
