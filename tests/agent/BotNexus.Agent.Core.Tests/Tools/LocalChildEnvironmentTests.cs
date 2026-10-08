using BotNexus.Agent.Core.Tools;

namespace BotNexus.Agent.Core.Tests.Tools;

public sealed class LocalChildEnvironmentTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Populate_ClearsAmbientSecretsAndCopiesOnlyExactEssentials(bool windows)
    {
        var ambient = new Dictionary<string, string?>
        {
            ["PATH"] = "bin", ["HOME"] = "home", ["SystemRoot"] = "windows",
            ["TMPDIR"] = "tmp", ["OPENAI_API_KEY"] = "synthetic-provider",
            ["BOTNEXUS_API_KEY"] = "synthetic-gateway", ["CUSTOM_TOKEN"] = "synthetic-custom",
            ["PATH_SECRET"] = "synthetic-prefix", ["HOME_TOKEN"] = "synthetic-prefix"
        };
        var target = new Dictionary<string, string?> { ["STALE_TOKEN"] = "synthetic-stale" };
        LocalChildEnvironment.Populate(target, ambient, windows: windows);
        target["PATH"].ShouldBe("bin");
        target.ShouldNotContainKey("STALE_TOKEN");
        target.ShouldNotContainKey("OPENAI_API_KEY");
        target.ShouldNotContainKey("BOTNEXUS_API_KEY");
        target.ShouldNotContainKey("CUSTOM_TOKEN");
        target.ShouldNotContainKey("PATH_SECRET");
        target.ShouldNotContainKey("HOME_TOKEN");
        target.ContainsKey("SystemRoot").ShouldBe(windows);
        target.ContainsKey("HOME").ShouldBe(!windows);
        target.ContainsKey("TMPDIR").ShouldBe(!windows);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Populate_PassThroughUsesPlatformCasingAndExplicitOverridesWin(bool windows)
    {
        var target = new Dictionary<string, string?>(StringComparer.Ordinal);
        var ambient = new Dictionary<string, string?> { ["PROBE"] = "ambient", ["NULL_PROBE"] = null };
        var policy = new LocalChildEnvironmentPolicy(["probe", "MISSING", "NULL_PROBE", "probe"]);
        LocalChildEnvironment.Populate(target, ambient, policy,
            new Dictionary<string, string> { ["probe"] = "explicit" }, windows);
        target.Count.ShouldBe(1);
        target["probe"].ShouldBe("explicit");
        target.ShouldNotContainKey("MISSING");
        target.ShouldNotContainKey("NULL_PROBE");
        LocalChildEnvironment.Populate(target, ambient, policy, windows: windows);
        target.ContainsKey("PROBE").ShouldBe(windows);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Populate_ReviewedEssentialsAndCasingAreExact(bool windows)
    {
        string[] expected = windows
            ? ["PATH", "PATHEXT", "SystemRoot", "WINDIR", "ComSpec", "TEMP", "TMP", "USERPROFILE", "HOMEDRIVE", "HOMEPATH", "APPDATA", "LOCALAPPDATA"]
            : ["PATH", "HOME", "TMPDIR", "TMP", "TEMP", "LANG", "LC_ALL", "LC_CTYPE", "TZ"];
        var ambient = expected.ToDictionary(name => name, name => (string?)"synthetic-essential", StringComparer.Ordinal);
        foreach (var name in expected) ambient[name + "_SECRET"] = "synthetic-prefix";
        var target = new Dictionary<string, string?>(StringComparer.Ordinal);
        LocalChildEnvironment.Populate(target, ambient, windows: windows);
        target.Keys.Order(StringComparer.Ordinal).ShouldBe(expected.Order(StringComparer.Ordinal));
        LocalChildEnvironment.Populate(target, new Dictionary<string, string?> { ["path"] = "synthetic-lowercase" }, windows: windows);
        target.ContainsKey("path").ShouldBe(windows);
        LocalChildEnvironment.Populate(target, new Dictionary<string, string?> { ["PATH"] = "ambient" },
            overrides: new Dictionary<string, string> { ["path"] = "explicit" }, windows: windows);
        target.Count.ShouldBe(windows ? 1 : 2);
        target["path"].ShouldBe("explicit");
        if (!windows) target["PATH"].ShouldBe("ambient");
    }

    [Theory]
    [InlineData("*")]
    [InlineData("TOKEN_*")]
    [InlineData("")]
    [InlineData(" BAD")]
    [InlineData("A=B")]
    [InlineData("A\u0000B")]
    public void Policy_RejectsNonExactNames(string name)
    {
        Should.Throw<ArgumentException>(() => new LocalChildEnvironmentPolicy([name]));
    }

    [Fact]
    public void Policy_SnapshotsNamesWithoutReadingValues()
    {
        string[] names = ["CUSTOM_TOKEN"];
        var policy = new LocalChildEnvironmentPolicy(names);
        names[0] = "OTHER_TOKEN";
        var target = new Dictionary<string, string?>();
        LocalChildEnvironment.Populate(target, new Dictionary<string, string?>
            { ["CUSTOM_TOKEN"] = "synthetic-custom", ["OTHER_TOKEN"] = "synthetic-other" }, policy);
        target["CUSTOM_TOKEN"].ShouldBe("synthetic-custom");
        target.ShouldNotContainKey("OTHER_TOKEN");
    }
}
