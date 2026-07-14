using BotNexus.Tools;

namespace BotNexus.CodingAgent.Tests.Security;

public sealed class ResourceLimitTests : IDisposable
{
    private readonly string _workingDirectory = Path.Combine(Path.GetTempPath(), $"botnexus-resource-limits-{Guid.NewGuid():N}");
    private readonly ShellTool _shellTool;
    private readonly WriteTool _writeTool;
    private readonly ReadTool _readTool;

    public ResourceLimitTests()
    {
        Directory.CreateDirectory(_workingDirectory);
        _shellTool = new ShellTool(_workingDirectory, defaultTimeoutSeconds: 1);
        _writeTool = new WriteTool(_workingDirectory);
        _readTool = new ReadTool(_workingDirectory);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ShellCommandTimeout_IsEnforced()
    {
        var result = await _shellTool.ExecuteAsync("t1", new Dictionary<string, object?>
        {
            ["command"] = "sleep 3"
        });

        var details = result.Details.ShouldBeOfType<ShellTool.ShellToolDetails>();
        details.TimedOut.ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ForkBombLikePattern_TimesOutInsteadOfHanging()
    {
        var result = await _shellTool.ExecuteAsync("t1", new Dictionary<string, object?>
        {
            ["command"] = "while true; do :; done"
        });

        var details = result.Details.ShouldBeOfType<ShellTool.ShellToolDetails>();
        details.TimedOut.ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("Category", "SecurityGap")]
    public async Task WriteTool_DoesNotEnforceDiskFillGuard_CurrentBehavior()
    {
        var largeContent = new string('x', 3 * 1024 * 1024);
        var result = await _writeTool.ExecuteAsync("t1", new Dictionary<string, object?>
        {
            ["path"] = "disk-fill-simulated.txt",
            ["content"] = largeContent
        });

        result.Content[0].Value.ShouldContain("Wrote 'disk-fill-simulated.txt'");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ReadTool_OnBinaryFile_ReturnsBoundedResponse()
    {
        var binaryPath = Path.Combine(_workingDirectory, "blob.bin");
        await File.WriteAllBytesAsync(binaryPath, Enumerable.Repeat((byte)0xFF, 8192).ToArray());

        var result = await _readTool.ExecuteAsync("t1", new Dictionary<string, object?> { ["path"] = "blob.bin" });
        result.Content.ShouldHaveSingleItem();
        result.Content[0].Value.Length.ShouldBeLessThan(60_000);
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("Category", "SecurityGap")]
    public async Task ConcurrentToolExecutions_HaveNoGlobalThrottle_CurrentBehavior()
    {
        var tasks = Enumerable.Range(0, 20)
            .Select(index => _writeTool.ExecuteAsync("t1", new Dictionary<string, object?>
            {
                ["path"] = $"f-{index}.txt",
                ["content"] = "ok"
            }));

        var results = await Task.WhenAll(tasks);
        results.Count().ShouldBe(20);
    }

    public void Dispose()
    {
        if (!Directory.Exists(_workingDirectory))
            return;

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Directory.Delete(_workingDirectory, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 4)
                    return;

                Thread.Sleep(500 * (attempt + 1));
            }
        }
    }
}
