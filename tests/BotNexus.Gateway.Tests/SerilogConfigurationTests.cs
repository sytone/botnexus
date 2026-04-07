using FluentAssertions;

namespace BotNexus.Gateway.Tests;

public sealed class SerilogConfigurationTests
{
    [Fact]
    public void SerilogRequestLogging_IsConfigured()
    {
        var programPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "gateway", "BotNexus.Gateway.Api", "Program.cs"));

        File.Exists(programPath).Should().BeTrue();

        var programSource = File.ReadAllText(programPath);
        programSource.Should().Contain("builder.Host.UseSerilog(");
        programSource.Should().Contain("app.UseSerilogRequestLogging();");
    }
}
