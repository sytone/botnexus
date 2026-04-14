using BotNexus.Probe;
using FluentAssertions;
using System.Reflection;

namespace BotNexus.Probe.Tests;

public sealed class ProbeOptionsTests
{
    [Fact]
    public void ParseArgs_WithNoArguments_UsesDefaults()
    {
        var parsed = InvokeParseArgs([]);

        parsed.Port.Should().Be(5050);
        parsed.GatewayUrl.Should().BeNull();
        parsed.LogsPath.Should().Contain(".botnexus");
        parsed.LogsPath.Should().Contain("logs");
        parsed.SessionsPath.Should().Contain(".botnexus");
        parsed.SessionsPath.Should().Contain("sessions");
        parsed.SessionDbPath.Should().Contain(".botnexus");
        parsed.SessionDbPath.Should().Contain("sessions.db");
        parsed.OtlpPort.Should().BeNull();
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
            "--otlp-port", "4318"
        ]);

        parsed.Port.Should().Be(6060);
        parsed.GatewayUrl.Should().Be("http://localhost:5010");
        parsed.LogsPath.Should().Be("C:\\logs");
        parsed.SessionsPath.Should().Be("C:\\sessions");
        parsed.SessionDbPath.Should().Be("C:\\sessions.db");
        parsed.OtlpPort.Should().Be(4318);
    }

    [Fact]
    public void ProbeOptions_RecordStoresProvidedValues()
    {
        var options = new ProbeOptions(5051, "http://gateway", "C:\\l", "C:\\s", "C:\\sessions.db", 4318);

        options.Port.Should().Be(5051);
        options.GatewayUrl.Should().Be("http://gateway");
        options.LogsPath.Should().Be("C:\\l");
        options.SessionsPath.Should().Be("C:\\s");
        options.SessionDbPath.Should().Be("C:\\sessions.db");
        options.OtlpPort.Should().Be(4318);
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
