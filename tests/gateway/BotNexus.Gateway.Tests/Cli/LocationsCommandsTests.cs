
namespace BotNexus.Gateway.Tests.Cli;

public sealed class LocationsCommandsTests
{
    [Fact]
    public async Task LocationsList_WithDeclaredLocation_ReturnsZero()
    {
        await using var fixture = await CliTestFixture.CreateAsync("""
            {
              "gateway": {
                "locations": {
                  "docs": {
                    "type": "filesystem",
                    "path": "Q:/repos/botnexus/docs",
                    "description": "Documentation"
                  }
                }
              }
            }
            """);

        var result = await fixture.RunCliAsync("locations", "list");

        result.ExitCode.ShouldBe(0);
        result.StdOut.ShouldContain("docs");
        result.StdOut.ShouldContain("Documentation");
    }

    [Fact]
    public async Task LocationsAddUpdateDelete_ModifiesConfig()
    {
        await using var fixture = await CliTestFixture.CreateAsync("""{"gateway":{"locations":{}}}""");
        var initialPath = Path.Combine(fixture.RootPath, "src").Replace('\\', '/');
        var updatedPath = Path.Combine(fixture.RootPath, "src-v2").Replace('\\', '/');

        var addResult = await fixture.RunCliAsync(
            "locations",
            "add",
            "repo",
            "--type",
            "filesystem",
            "--path",
            initialPath,
            "--description",
            "Repository");
        var updateResult = await fixture.RunCliAsync("locations", "update", "repo", "--path", updatedPath, "--description", "Repository v2");
        var deleteResult = await fixture.RunCliAsync("locations", "delete", "repo");
        var config = await fixture.LoadConfigAsync();

        addResult.ExitCode.ShouldBe(0);
        updateResult.ExitCode.ShouldBe(0);
        deleteResult.ExitCode.ShouldBe(0);
        config.Gateway?.Locations.ShouldNotContainKey("repo");
    }

    [Fact]
    public async Task LocationsDelete_WithFileAccessReferences_Warns()
    {
        await using var fixture = await CliTestFixture.CreateAsync("""
            {
              "gateway": {
                "locations": {
                  "docs": {
                    "type": "filesystem",
                    "path": "Q:/repos/botnexus/docs"
                  }
                },
                "fileAccess": {
                  "allowedReadPaths": [ "@docs" ]
                }
              },
              "agents": {
                "assistant": {
                  "provider": "copilot",
                  "model": "gpt-4.1",
                  "fileAccess": {
                    "allowedWritePaths": [ "@docs/output" ]
                  }
                }
              }
            }
            """);

        var result = await fixture.RunCliAsync("locations", "delete", "docs");

        result.ExitCode.ShouldBe(0);
        result.StdOut.ShouldContain("docs");
        result.StdOut.ShouldContain("referenced by fileAccess policies");
        result.StdOut.ShouldContain("gateway.fileAccess.allowedReadPaths[0]");
        result.StdOut.ShouldContain("agents.assistant.fileAccess.allowedWritePaths[0]");
    }

    [Fact]
    public async Task DoctorLocations_WithExistingAndMissingFilesystemPaths_ReportsStatus()
    {
        await using var fixture = await CliTestFixture.CreateAsync();
        var existingPath = Path.Combine(fixture.RootPath, "existing");
        var missingPath = Path.Combine(fixture.RootPath, "missing");
        Directory.CreateDirectory(existingPath);

        await File.WriteAllTextAsync(
            fixture.ConfigPath,
            $$"""
              {
                "gateway": {
                  "locations": {
                    "exists": {
                      "type": "filesystem",
                      "path": "{{existingPath.Replace('\\', '/')}}"
                    },
                    "missing": {
                      "type": "filesystem",
                      "path": "{{missingPath.Replace('\\', '/')}}"
                    }
                  }
                }
              }
              """);

        var result = await fixture.RunCliAsync("doctor", "locations");

        result.ExitCode.ShouldBe(1);
        result.StdOut.ShouldContain("Checking");
        result.StdOut.ShouldContain("exists");
        result.StdOut.ShouldContain("missing");
        result.StdOut.ShouldContain("not found");
    }
}
