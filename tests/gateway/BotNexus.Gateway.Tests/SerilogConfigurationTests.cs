
namespace BotNexus.Gateway.Tests;

public sealed class SerilogConfigurationTests
{
    [Fact]
    public void SerilogRequestLogging_IsConfigured()
    {
        var programPath = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "gateway",
            "BotNexus.Gateway.Api",
            "Program.cs");

        File.Exists(programPath).ShouldBeTrue();

        var programSource = File.ReadAllText(programPath);
        programSource.ShouldContain("builder.Host.UseSerilog(");
        programSource.ShouldContain("app.UseSerilogRequestLogging();");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Packages.props")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate Directory.Packages.props from test base directory.");
    }
}
