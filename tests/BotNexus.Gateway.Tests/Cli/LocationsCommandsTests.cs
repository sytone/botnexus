using FluentAssertions;

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

        result.ExitCode.Should().Be(0);
        result.StdOut.Should().Contain("docs");
        result.StdOut.Should().Contain("Documentation");
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

        addResult.ExitCode.Should().Be(0);
        updateResult.ExitCode.Should().Be(0);
        deleteResult.ExitCode.Should().Be(0);
        config.Gateway?.Locations.Should().NotContainKey("repo");
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

        result.ExitCode.Should().Be(0);
        result.StdOut.Should().Contain("Warning: Location 'docs' is referenced by fileAccess policies");
        result.StdOut.Should().Contain("gateway.fileAccess.allowedReadPaths[0]");
        result.StdOut.Should().Contain("agents.assistant.fileAccess.allowedWritePaths[0]");
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

        result.ExitCode.Should().Be(1);
        result.StdOut.Should().Contain("Checking");
        result.StdOut.Should().Contain("exists");
        result.StdOut.Should().Contain("missing");
        result.StdOut.Should().Contain("not found");
    }
}
