using System.Diagnostics;
using System.Text.Json;
using BotNexus.Agent.Core.Types;
using BotNexus.Extensions.ProcessTool;

namespace BotNexus.Extensions.ProcessTool.Tests;

/// <summary>
/// Verifies that <see cref="ProcessTool"/>'s <c>output</c> action clamps the caller-supplied
/// <c>tail</c> to a configured ceiling, and tolerates out-of-range JSON numbers instead of
/// throwing. Without the clamp, an agent could request an enormous tail and pull the entire
/// captured ring buffer into one tool result.
/// </summary>
public sealed class ProcessToolTailCeilingTests : IDisposable
{
    private readonly ProcessManager _manager = new();
    private readonly List<ManagedProcess> _spawned = [];

    public void Dispose()
    {
        _manager.Clear();
        foreach (var p in _spawned)
            p.Dispose();
    }

    [Fact]
    public async Task Output_TailAboveCeiling_IsClampedToMaxTail()
    {
        var tool = new ProcessTool(_manager, new ProcessToolOptions { MaxTail = 3 });
        var managed = SpawnFiveLineProcess();
        managed.WaitForExit(5_000);
        await WaitForOutputAsync(tool, managed.Pid, "line5");

        // Request 1000 lines but MaxTail is 3 -> only the last few lines should come back,
        // and the earliest lines must be excluded by the ceiling.
        var result = await tool.ExecuteAsync("c1", Args("output", pid: managed.Pid, tail: 1000));
        var text = ResultText(result);

        text.ShouldContain("line5");
        text.ShouldContain("line4");
        text.ShouldNotContain("line1");
        text.ShouldNotContain("line2");
    }

    [Fact]
    public async Task Output_OutOfRangeTailNumber_DoesNotThrowAndIsClamped()
    {
        var tool = new ProcessTool(_manager, new ProcessToolOptions { MaxTail = 3 });
        var managed = SpawnFiveLineProcess();
        managed.WaitForExit(5_000);
        await WaitForOutputAsync(tool, managed.Pid, "line5");

        // A JSON number larger than int.MaxValue would throw from GetInt32(); it must be tolerated
        // and then bounded by MaxTail (3) so line1/line2 are excluded.
        var args = ParseArgs($$"""{ "action": "output", "pid": {{managed.Pid}}, "tail": 99999999999 }""");
        var result = await tool.ExecuteAsync("c2", args);
        var text = ResultText(result);

        text.ShouldContain("line5");
        text.ShouldNotContain("line1");
    }

    [Fact]
    public async Task Output_NonPositiveTail_StillReturnsFullOutput()
    {
        // Preserve the documented convention: tail <= 0 means "return full output". The ceiling
        // bounds only the upper end, so a non-positive sentinel must be unaffected.
        var tool = new ProcessTool(_manager, new ProcessToolOptions { MaxTail = 3 });
        var managed = SpawnFiveLineProcess();
        managed.WaitForExit(5_000);
        await WaitForOutputAsync(tool, managed.Pid, "line5");

        var result = await tool.ExecuteAsync("c3", Args("output", pid: managed.Pid, tail: 0));
        var text = ResultText(result);

        text.ShouldContain("line1");
        text.ShouldContain("line5");
    }

    [Fact]
    public async Task Output_TailBelowCeiling_IsHonoured()
    {
        // A positive tail under the ceiling passes through unchanged.
        var tool = new ProcessTool(_manager, new ProcessToolOptions { MaxTail = 100 });
        var managed = SpawnFiveLineProcess();
        managed.WaitForExit(5_000);
        await WaitForOutputAsync(tool, managed.Pid, "line5");

        var result = await tool.ExecuteAsync("c4", Args("output", pid: managed.Pid, tail: 2));
        var text = ResultText(result);

        text.ShouldContain("line5");
        text.ShouldNotContain("line1");
    }

    [Fact]
    public void Default_MaxTail_IsTenThousand()
        => ProcessToolOptions.Default.MaxTail.ShouldBe(10_000);

    // ───────────── helpers ─────────────

    private ManagedProcess SpawnFiveLineProcess()
    {
        var (file, args) = OperatingSystem.IsWindows()
            ? ("cmd.exe", "/c echo line1 & echo line2 & echo line3 & echo line4 & echo line5")
            : ("/bin/bash", "-lc \"echo line1; echo line2; echo line3; echo line4; echo line5\"");

        var psi = new ProcessStartInfo
        {
            FileName = file,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        var process = Process.Start(psi)!;
        var managed = new ManagedProcess(process, "five-line", DateTimeOffset.UtcNow);
        _spawned.Add(managed);
        _manager.Register(process.Id, managed);
        return managed;
    }

    private static async Task WaitForOutputAsync(ProcessTool tool, int pid, string expected)
    {
        var timeoutAt = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < timeoutAt)
        {
            var result = await tool.ExecuteAsync("wait", Args("output", pid: pid, tail: 100));
            if (ResultText(result).Contains(expected, StringComparison.Ordinal))
                return;
            await Task.Delay(100);
        }
    }

    private static IReadOnlyDictionary<string, object?> Args(string action, int? pid = null, int? tail = null)
    {
        var dict = new Dictionary<string, object?> { ["action"] = action };
        if (pid is not null) dict["pid"] = pid;
        if (tail is not null) dict["tail"] = tail;
        return dict;
    }

    private static IReadOnlyDictionary<string, object?> ParseArgs(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var dict = new Dictionary<string, object?>();
        foreach (var prop in doc.RootElement.EnumerateObject())
            dict[prop.Name] = prop.Value.Clone();
        return dict;
    }

    private static string ResultText(AgentToolResult result)
        => string.Join("", result.Content.Select(c => c.Value));
}
