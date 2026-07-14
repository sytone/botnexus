using System.Threading;

namespace BotNexus.CodingAgent.Tests.Tools;

public sealed class ToolTempDirectoryFixture : IDisposable
{
    private readonly string _rootDirectory = Path.Combine(Path.GetTempPath(), $"botnexus-tool-tests-{Guid.NewGuid():N}");
    private int _nextDirectoryId;

    public ToolTempDirectoryFixture()
    {
        Directory.CreateDirectory(_rootDirectory);
    }

    public string CreateDirectory(string prefix)
    {
        var nextId = Interlocked.Increment(ref _nextDirectoryId);
        var directory = Path.Combine(_rootDirectory, $"{prefix}-{nextId}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootDirectory))
        {
            Directory.Delete(_rootDirectory, recursive: true);
        }
    }
}
