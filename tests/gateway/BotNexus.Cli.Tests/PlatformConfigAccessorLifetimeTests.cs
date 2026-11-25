using BotNexus.Cli.Services;
using Shouldly;

namespace BotNexus.Cli.Tests;

public sealed class PlatformConfigAccessorLifetimeTests
{
    [Fact]
    public void RepeatedPhysicalReads_DoNotRetainInotifyInstances()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var directory = Path.Combine(Path.GetTempPath(), $"botnexus-config-lifetime-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "config.json");
            File.WriteAllText(path, "{}");
            var accessor = new PlatformConfigAccessor();
            var before = CountInotifyInstances();

            for (var i = 0; i < 20; i++)
            {
                accessor.Get(path).ShouldNotBeNull();
                accessor.GetFromHome(directory).ShouldNotBeNull();
            }

            CountInotifyInstances().ShouldBeLessThanOrEqualTo(before);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static int CountInotifyInstances() =>
        Directory.EnumerateFiles("/proc/self/fd")
            .Count(path => new FileInfo(path).LinkTarget == "anon_inode:inotify");
}
