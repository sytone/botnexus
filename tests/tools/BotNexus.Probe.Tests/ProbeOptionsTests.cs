using BotNexus.Probe;
using System.Reflection;

namespace BotNexus.Probe.Tests;

public sealed class ProbeOptionsTests
{
    [Fact]
    public void ParseArgs_WithNoArguments_UsesDefaults()
    {
        var parsed = InvokeParseArgs([]);

        parsed.Port.ShouldBe(5050);
        parsed.GatewayUrl.ShouldBeNull();
        parsed.LogsPath.ShouldContain(".botnexus");
        parsed.LogsPath.ShouldContain("logs");
        parsed.SessionsPath.ShouldContain(".botnexus");
        parsed.SessionsPath.ShouldContain("sessions");
        parsed.SessionDbPath.ShouldContain(".botnexus");
        parsed.SessionDbPath.ShouldContain("sessions.db");
        parsed.OtlpPort.ShouldBeNull();
        parsed.ListenAny.ShouldBeFalse();
    }

    [Fact]
    public void ParseArgs_WithCustomArguments_MapsValues()
    {
        var parsed = InvokeParseArgs([
            "--port", "6060",
            "--gateway", "http://localhost:5010",
            "--logs", "C:\\logs",
            "--sessions", "C:\\sessions",
            "--session-db", "C:\\sessions.db",
            "--otlp-port", "4318",
            "--listen-any"
        ]);

        parsed.Port.ShouldBe(6060);
        parsed.GatewayUrl.ShouldBe("http://localhost:5010");
        parsed.LogsPath.ShouldBe("C:\\logs");
        parsed.SessionsPath.ShouldBe("C:\\sessions");
        parsed.SessionDbPath.ShouldBe("C:\\sessions.db");
        parsed.OtlpPort.ShouldBe(4318);
        parsed.ListenAny.ShouldBeTrue();
    }

    [Fact]
    public void ProbeOptions_RecordStoresProvidedValues()
    {
        var options = new ProbeOptions(5051, "http://gateway", "C:\\l", "C:\\s", "C:\\sessions.db", 4318, true);

        options.Port.ShouldBe(5051);
        options.GatewayUrl.ShouldBe("http://gateway");
        options.LogsPath.ShouldBe("C:\\l");
        options.SessionsPath.ShouldBe("C:\\s");
        options.SessionDbPath.ShouldBe("C:\\sessions.db");
        options.OtlpPort.ShouldBe(4318);
        options.ListenAny.ShouldBeTrue();
    }

    private static ProbeOptions InvokeParseArgs(string[] args)
    {
        var assembly = typeof(ProbeOptions).Assembly;
        var programType = assembly.GetType("Program")!;
        var parseMethod = programType.GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(method => method.Name.Contains("ParseArgs", StringComparison.Ordinal));
        return (ProbeOptions)parseMethod.Invoke(null, [args])!;
    }
}
