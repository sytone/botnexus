using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Determinism guard for the WASM build-output fence (#2707).
///
/// <para>
/// <c>WasmBuildOutput_ContainsNoNonFrameworkLeakedAssemblies</c> used to read
/// <c>scannedAny should be True but was False</c> in a fresh worktree and pass in a checkout where
/// some unrelated project had previously been built. That is a test whose verdict is a function of
/// build history rather than of source, and it cost a full falsification cycle on PR #2703.
/// </para>
///
/// <para>
/// The fix keeps the anti-vacuity guard and separates the two cases the old code conflated:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <b>No build output at all</b> - none of the WASM entry points has a <c>bin</c> directory.
///     Nothing was ever produced to inspect, so the fence SKIPS with an explicit reason naming the
///     missing directories. A skip that says why is honest; a silent empty pass is not.
///   </description></item>
///   <item><description>
///     <b>Build output expected but empty</b> - a <c>bin</c> directory exists yet contains no
///     managed assemblies. That is a genuine anomaly, not a fresh checkout, and it still FAILS. This
///     is the original anti-vacuity guard, preserved and now aimed at the case it was actually for.
///   </description></item>
/// </list>
///
/// <para>
/// These tests drive <see cref="WasmPayloadDependencyArchitectureTests.ScanWasmBuildOutput"/>
/// directly over synthetic directories, so they assert the fresh-checkout path itself rather than
/// depending on the state of the machine they run on. They are the reason the #2707 regression
/// cannot silently return.
/// </para>
/// </summary>
public sealed class WasmBuildOutputScanDeterminismTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "botnexus-wasm-scan-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string BinRoot(string entry, bool create)
    {
        var path = Path.Combine(_root, entry, "bin");
        if (create)
        {
            Directory.CreateDirectory(path);
        }

        return path;
    }

    private static void WriteAssembly(string binRoot, string relativeName)
    {
        var full = Path.Combine(binRoot, relativeName);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "not a real assembly - only the file name is read");
    }

    [Fact]
    public void Scan_FreshCheckoutWithNoBinDirectories_ReportsNoBuildOutputRatherThanFailing()
    {
        var roots = new[] { BinRoot("Desktop", create: false), BinRoot("Mobile", create: false) };

        var result = WasmPayloadDependencyArchitectureTests.ScanWasmBuildOutput(
            roots,
            p => p);

        result.State.ShouldBe(
            WasmBuildOutputScanState.NoBuildOutput,
            "A fresh worktree in which no WASM entry point has ever been built must be reported as " +
            "'nothing was produced to scan' (-> explicit skip), NOT as a violated invariant. This " +
            "is #2707 acceptance criterion 2.");
        result.AssembliesScanned.ShouldBe(0);
        result.Offenders.ShouldBeEmpty();
        result.MissingBinRoots.Count.ShouldBe(
            2,
            "#2707 acceptance criterion 3: the skip must name the missing artifacts, so a " +
            "permanently-skipping fence cannot masquerade as a passing one.");
        result.MissingBinRoots.ShouldBe(roots, ignoreOrder: true);
    }

    [Fact]
    public void Scan_BinDirectoryExistsButContainsNoAssemblies_StillTripsTheAntiVacuityGuard()
    {
        var built = BinRoot("Desktop", create: true);

        var result = WasmPayloadDependencyArchitectureTests.ScanWasmBuildOutput(
            new[] { built },
            p => p);

        result.State.ShouldBe(
            WasmBuildOutputScanState.Scanned,
            "#2707 acceptance criterion 5: build output was EXPECTED here (the directory exists), " +
            "so this is not the fresh-checkout case and must not be downgraded to a skip.");
        result.AssembliesScanned.ShouldBe(
            0,
            "An existing bin directory containing zero assemblies is a genuine anomaly. The caller's " +
            "non-vacuity assertion must still fail on it - that guard is not being weakened.");
        result.MissingBinRoots.ShouldBeEmpty();
    }

    [Fact]
    public void Scan_PreviouslyBuiltCheckoutWithCleanOutput_ScansAndFindsNoOffenders()
    {
        var built = BinRoot("Desktop", create: true);
        WriteAssembly(built, Path.Combine("Debug", "net10.0", "System.Text.Json.dll"));
        WriteAssembly(built, Path.Combine("Debug", "net10.0", "Microsoft.AspNetCore.SignalR.Client.dll"));
        WriteAssembly(built, Path.Combine("Debug", "net10.0", "BotNexus.Domain.Wire.dll"));

        var result = WasmPayloadDependencyArchitectureTests.ScanWasmBuildOutput(
            new[] { built },
            p => p);

        result.State.ShouldBe(WasmBuildOutputScanState.Scanned);
        result.AssembliesScanned.ShouldBe(3);
        result.Offenders.ShouldBeEmpty();
    }

    [Fact]
    public void Scan_LeakedAssemblyInOutput_IsReportedAsAnOffender()
    {
        var built = BinRoot("Desktop", create: true);
        WriteAssembly(built, Path.Combine("Debug", "net10.0", "System.Text.Json.dll"));
        WriteAssembly(built, Path.Combine("Debug", "net10.0", "Vogen.SharedTypes.dll"));

        var result = WasmPayloadDependencyArchitectureTests.ScanWasmBuildOutput(
            new[] { built },
            p => p);

        result.State.ShouldBe(WasmBuildOutputScanState.Scanned);
        result.AssembliesScanned.ShouldBe(2);
        result.Offenders.Count.ShouldBe(
            1,
            "The determinism fix must not blunt the fence: the assembly that actually leaked in " +
            "#2328 has to still be named as an offender.");
        result.Offenders[0].ShouldContain("Vogen.SharedTypes");
    }

    [Fact]
    public void Scan_OneEntryBuiltAndOneNot_ScansRatherThanSkipping()
    {
        var built = BinRoot("Desktop", create: true);
        WriteAssembly(built, Path.Combine("Debug", "net10.0", "System.Private.CoreLib.dll"));
        var missing = BinRoot("Mobile", create: false);

        var result = WasmPayloadDependencyArchitectureTests.ScanWasmBuildOutput(
            new[] { built, missing },
            p => p);

        result.State.ShouldBe(
            WasmBuildOutputScanState.Scanned,
            "Partial build output is still output. The fence only skips when NOTHING was produced " +
            "anywhere - otherwise a half-built checkout would silently stop guarding the half that " +
            "does exist.");
        result.AssembliesScanned.ShouldBe(1);
        result.MissingBinRoots.ShouldHaveSingleItem().ShouldBe(missing);
    }

    [Fact]
    public void Scan_IsAFunctionOfDirectoryContentOnly_SoRepeatedCallsAgree()
    {
        var built = BinRoot("Desktop", create: true);
        WriteAssembly(built, Path.Combine("Debug", "net10.0", "System.Text.Json.dll"));

        var first = WasmPayloadDependencyArchitectureTests.ScanWasmBuildOutput(new[] { built }, p => p);
        var second = WasmPayloadDependencyArchitectureTests.ScanWasmBuildOutput(new[] { built }, p => p);

        second.State.ShouldBe(first.State);
        second.AssembliesScanned.ShouldBe(first.AssembliesScanned);
        second.Offenders.ShouldBe(first.Offenders);
    }

    /// <summary>
    /// #2707 acceptance criterion 4 - staleness is EXPLICITLY OUT OF SCOPE, and this test pins that
    /// statement so the limitation cannot quietly be forgotten or mis-sold as solved. The scan reads
    /// whatever assemblies are on disk; it cannot tell output built from the current commit from
    /// output left by an earlier one. The fence therefore documents the limitation in its own
    /// failure text rather than implying a freshness guarantee it does not provide.
    /// </summary>
    [Fact]
    public void Scan_DoesNotClaimToDetectStaleOutput()
    {
        var built = BinRoot("Desktop", create: true);
        WriteAssembly(built, Path.Combine("Debug", "net10.0", "System.Text.Json.dll"));
        File.SetLastWriteTimeUtc(
            Path.Combine(built, "Debug", "net10.0", "System.Text.Json.dll"),
            new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var result = WasmPayloadDependencyArchitectureTests.ScanWasmBuildOutput(new[] { built }, p => p);

        result.State.ShouldBe(
            WasmBuildOutputScanState.Scanned,
            "Decades-old output is still scanned as-is. The fence makes no freshness claim; " +
            "currency of the artifact is the build's responsibility, not this scan's.");
        result.AssembliesScanned.ShouldBe(1);

        // The limitation must be stated in words the failure output carries, so nobody reads a green
        // here as "the payload matches this commit".
        WasmPayloadDependencyArchitectureTests.StalenessScopeStatement.ShouldContain("stale");
        WasmPayloadDependencyArchitectureTests.StalenessScopeStatement.ShouldContain("out of scope");
    }
}
