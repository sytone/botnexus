using BotNexus.Cli.Commands.Doctor;
using BotNexus.Domain.World;
using Shouldly;

namespace BotNexus.Cli.Tests;

/// <summary>
/// #2836: <c>doctor</c> must report a filesystem location that belongs to another world, rather than
/// calling it accessible.
/// </summary>
/// <remarks>
/// This is the observability half of the sentinel. The resolver refuses a foreign home at startup;
/// <c>doctor</c> is how an operator finds out <i>which</i> configured location is misdirected without
/// having to start a process against it. It reports and never throws - a diagnostic that fails is a
/// diagnostic that stops diagnosing.
/// </remarks>
public sealed class LocationProbeWorldSentinelTests : IDisposable
{
    private const string WorldA = "11111111-1111-1111-1111-111111111111";
    private const string WorldB = "22222222-2222-2222-2222-222222222222";

    private readonly string _root = Directory.CreateTempSubdirectory("bn-2836-").FullName;

    [Fact]
    public void ForeignSentinel_IsReportedAndNamesBothWorlds()
    {
        WriteSentinel(WorldB);

        var message = LocationProbe.DescribeForeignWorld(_root, WorldA);

        message.ShouldNotBeNull();
        message!.ShouldContain(WorldB);
        message.ShouldContain(WorldA);
    }

    [Fact]
    public void MatchingSentinel_IsNotReported()
    {
        WriteSentinel(WorldA);

        LocationProbe.DescribeForeignWorld(_root, WorldA).ShouldBeNull();
    }

    [Fact]
    public void AbsentSentinel_IsNotReported()
        => LocationProbe.DescribeForeignWorld(_root, WorldA).ShouldBeNull(
            "a directory with no sentinel presents no competing identity; reporting it would make " +
            "every pre-existing home look broken and bury the one that genuinely is.");

    [Fact]
    public void MalformedSentinel_IsNotReported()
    {
        File.WriteAllText(Path.Combine(_root, WorldSentinel.FileName), "{ this is not json");

        LocationProbe.DescribeForeignWorld(_root, WorldA).ShouldBeNull();
    }

    [Fact]
    public void WithoutAWorldId_TheCheckIsInert()
    {
        WriteSentinel(WorldB);

        LocationProbe.DescribeForeignWorld(_root, worldId: null).ShouldBeNull();
    }

    [Fact]
    public void MissingDirectory_IsNotReported()
        => LocationProbe.DescribeForeignWorld(Path.Combine(_root, "nope"), WorldA).ShouldBeNull();

    private void WriteSentinel(string worldId)
        => File.WriteAllText(
            Path.Combine(_root, WorldSentinel.FileName),
            WorldSentinel.Serialize(worldId, "1.0.0.0"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best effort temp cleanup */ }
    }
}
