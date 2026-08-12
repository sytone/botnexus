using System.IO.Abstractions.TestingHelpers;
using BotNexus.Cli.Commands.Doctor;
using Shouldly;

namespace BotNexus.Cli.Tests.Commands.Doctor;

/// <summary>
/// Tests for the <c>botnexus doctor</c> world-identity section (#2834, acceptance criterion 6): the
/// output must show the resolved world ID alongside the resolved home path, so an operator running a
/// dev, a test and the live gateway on one machine can tell at a glance which world a process
/// believes it is in.
/// </summary>
public sealed class WorldIdCheckTests
{
    private const string ConfigPath = "/srv/botnexus-world/config.json";
    private const string HomePath = "/srv/botnexus-world";

    [Fact]
    public async Task Reports_ResolvedWorldId_AndResolvedHomePath()
    {
        const string worldId = "9c3a7c1e-4d2b-4f18-9f0a-2c5b6d7e8f90";
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [ConfigPath] = new($$"""{ "version": 1, "worldId": "{{worldId}}" }"""),
        });

        var result = await new WorldIdCheck(fileSystem)
            .RunAsync(new DoctorCheckContext(ConfigPath, HomePath, Verbose: false), CancellationToken.None);

        result.Outcome.ShouldBe(DoctorOutcome.Healthy);
        var rendered = string.Join("\n", new[] { result.Summary }.Concat(result.Details));
        rendered.ShouldContain(worldId);
        rendered.ShouldContain(HomePath);
    }

    [Fact]
    public async Task WarnsWhenHomeHasNoWorldIdYet()
    {
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [ConfigPath] = new("""{ "version": 1 }"""),
        });

        var result = await new WorldIdCheck(fileSystem)
            .RunAsync(new DoctorCheckContext(ConfigPath, HomePath, Verbose: false), CancellationToken.None);

        result.Outcome.ShouldBe(DoctorOutcome.Warning);
        string.Join("\n", result.Details).ShouldContain(HomePath);
    }

    [Fact]
    public async Task ErrorsWhenConfigIsMissing()
    {
        var result = await new WorldIdCheck(new MockFileSystem())
            .RunAsync(new DoctorCheckContext(ConfigPath, HomePath, Verbose: false), CancellationToken.None);

        result.Outcome.ShouldBe(DoctorOutcome.Error);
    }

    /// <summary>
    /// The check must be part of the default suite, otherwise a bare <c>botnexus doctor</c> would
    /// silently omit it - exactly the hardcoded-parent-handler failure #2041's registry exists to stop.
    /// </summary>
    [Fact]
    public void IsRegisteredInTheDefaultDoctorSuite()
        => DoctorCheckRegistry.CreateDefault().ShouldContain(check => check is WorldIdCheck);
}
