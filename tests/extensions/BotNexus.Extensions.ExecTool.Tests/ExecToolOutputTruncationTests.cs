using System.Runtime.InteropServices;
using System.Text;
using BotNexus.Agent.Core.Types;
using BotNexus.Extensions.ExecTool;
using Shouldly;
using System.IO.Abstractions.TestingHelpers;

namespace BotNexus.Extensions.ExecTool.Tests;

/// <summary>
/// Covers issue #2895: the exec output retention cap used to announce itself with a fixed
/// <c>[output truncated at 100KB]</c> banner that restated a compile-time constant and disclosed
/// nothing about the actual loss. A caller could not tell whether one line or fifty megabytes went
/// missing, so it could not decide whether to re-run with a narrower command.
///
/// The banner now reports the real quantities. Note the quantity the issue expected to reuse -
/// <c>totalBytes</c> - is the RETAINED byte count, not the produced total: over-cap lines were
/// dropped without ever being counted, so the discarded volume had to be measured explicitly.
/// </summary>
[Collection(ExecToolBackgroundRegistryCollection.Name)]
public class ExecToolOutputTruncationTests : IDisposable
{
    private readonly ExecTool _tool = new(workingDirectory: null, fileSystem: new MockFileSystem());

    public void Dispose() => ExecTool.ClearBackgroundProcesses();

    private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    private static string GetResultText(AgentToolResult result)
    {
        result.Content.ShouldNotBeEmpty();
        return result.Content[0].Value;
    }

    private static IReadOnlyDictionary<string, object?> BuildArgs(string[] command) =>
        new Dictionary<string, object?> { ["command"] = command };

    /// <summary>
    /// AC4. Drives a real child process that emits a known volume well over the retention cap and
    /// asserts the banner's discarded figure equals the true overage, computed independently from
    /// the same accounting rule the collector uses (UTF-8 payload bytes plus one newline per line).
    ///
    /// Uniform line lengths make this exact: once a line no longer fits, no later line of the same
    /// length can fit either, so every remaining line is discarded whole.
    /// </summary>
    [Fact]
    public async Task Execute_OutputOverCap_ReportsExactDiscardedAndRetainedBytes()
    {
        const int LineLength = 1000;
        const int LineCount = 150;

        var lineBytes = LineLength + Environment.NewLine.Length;
        var retainedLines = ExecTool.MaxOutputBytesForTest / lineBytes;
        var expectedRetained = retainedLines * lineBytes;
        var expectedDiscarded = (LineCount - retainedLines) * lineBytes;

        // Guard the fixture itself: the scenario is only meaningful if it genuinely overflows.
        expectedDiscarded.ShouldBeGreaterThan(0);

        var payloadPath = Path.Combine(Path.GetTempPath(), $"botnexus-2895-{Guid.NewGuid():N}.txt");
        var line = new string('x', LineLength);
        await File.WriteAllTextAsync(
            payloadPath,
            // No trailing newline: a final empty line would add an uncounted record to the stream.
            string.Join("\n", Enumerable.Repeat(line, LineCount)),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        try
        {
            string[] command = IsWindows
                ? ["cmd.exe", "/c", $"type \"{payloadPath}\""]
                : ["/bin/bash", "-c", $"cat \"{payloadPath}\""];

            var result = await _tool.ExecuteAsync("test-2895-overflow", BuildArgs(command));
            var text = GetResultText(result);

            // AC1: both quantities are present, and the discarded figure is the real overage.
            text.ShouldContain($"discarded {expectedDiscarded} bytes");
            text.ShouldContain($"retained {expectedRetained} bytes");

            // AC2: the banner says WHICH portion survived.
            text.Contains("(head)", StringComparison.Ordinal)
                .ShouldBeTrue("the banner must name the retained portion");

            // The banner leads the payload so it is visible before any output is read.
            text.Contains(ExecTool.TruncationBannerPrefix, StringComparison.Ordinal)
                .ShouldBeTrue("the banner must lead the payload");
        }
        finally
        {
            File.Delete(payloadPath);
        }
    }

    /// <summary>
    /// AC3. A run whose output sits comfortably under the cap must be byte-identical to the
    /// pre-change behaviour, which means no banner text of any kind.
    /// </summary>
    [Fact]
    public async Task Execute_OutputUnderCap_EmitsNoBanner()
    {
        string[] command = IsWindows
            ? ["cmd.exe", "/c", "echo small-output"]
            : ["/bin/bash", "-c", "echo small-output"];

        var result = await _tool.ExecuteAsync("test-2895-under", BuildArgs(command));
        var text = GetResultText(result);

        text.Trim().ShouldBe("small-output");
        text.Contains(ExecTool.TruncationBannerPrefix, StringComparison.Ordinal)
            .ShouldBeFalse("output below the cap must not be annotated at all");
        text.Contains("truncated", StringComparison.OrdinalIgnoreCase)
            .ShouldBeFalse("output below the cap must not be annotated at all");
    }

    /// <summary>
    /// AC1 + AC2 pinned directly on the formatter so the wording contract is asserted independently
    /// of process scheduling: both quantities, the produced total, and the retained portion.
    /// </summary>
    [Fact]
    public void FormatTruncationBanner_StatesDiscardedRetainedAndPortion()
    {
        var banner = ExecTool.FormatTruncationBanner(retainedBytes: 102_204, discardedBytes: 48_096);

        banner.ShouldContain("discarded 48096 bytes");
        banner.ShouldContain("retained 102204 bytes");
        banner.ShouldContain("(head)");
        banner.ShouldContain("150300");
        banner.ShouldStartWith(ExecTool.TruncationBannerPrefix);
    }
}
