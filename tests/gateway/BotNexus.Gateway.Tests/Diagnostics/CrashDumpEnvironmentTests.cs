using BotNexus.Gateway.Diagnostics;

namespace BotNexus.Gateway.Tests.Diagnostics;

public sealed class CrashDumpEnvironmentTests
{
    [Fact]
    public void BuildVariables_UsesDumpsDirectoryInMiniDumpName()
    {
        var dumpsDir = Path.Combine(Path.GetTempPath(), "botnexus-dumps");

        var vars = CrashDumpEnvironment.BuildVariables(dumpsDir);

        vars["DOTNET_DbgEnableMiniDump"].ShouldBe("1");
        vars["DOTNET_DbgMiniDumpType"].ShouldBe("2");
        // The dump name template must live under the configured dumps directory.
        var name = vars["DOTNET_DbgMiniDumpName"];
        Path.GetFullPath(Path.GetDirectoryName(name)!)
            .ShouldBe(Path.GetFullPath(dumpsDir));
        // A %d placeholder keeps each crash dump unique per PID.
        name.ShouldContain("%d");
    }

    [Fact]
    public void BuildVariables_NullOrWhitespaceDirectory_Throws()
    {
        Should.Throw<ArgumentException>(() => CrashDumpEnvironment.BuildVariables("  "));
        Should.Throw<ArgumentException>(() => CrashDumpEnvironment.BuildVariables(null!));
    }

    [Fact]
    public void Apply_SetsEachVariableViaSetter_AndSwallowsSetterFailure()
    {
        var captured = new Dictionary<string, string>();
        var dumpsDir = Path.Combine(Path.GetTempPath(), "botnexus-dumps");

        // A throwing setter must not escape - diagnostics wiring must never break startup.
        var applied = CrashDumpEnvironment.Apply(dumpsDir, (key, value) =>
        {
            if (key == "DOTNET_DbgMiniDumpType")
                throw new InvalidOperationException("boom");
            captured[key] = value;
        });

        applied.ShouldBeFalse();
        captured["DOTNET_DbgEnableMiniDump"].ShouldBe("1");
    }

    [Fact]
    public void Apply_AllSettersSucceed_ReturnsTrue()
    {
        var captured = new Dictionary<string, string>();
        var dumpsDir = Path.Combine(Path.GetTempPath(), "botnexus-dumps");

        var applied = CrashDumpEnvironment.Apply(dumpsDir, (key, value) => captured[key] = value);

        applied.ShouldBeTrue();
        captured.Count.ShouldBe(3);
    }
}
