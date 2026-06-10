using BotNexus.Cron.Actions;
using BotNexus.Domain.Primitives;

namespace BotNexus.Cron.Tests;

public sealed class MemoryDreamingCronActionTests
{
    [Fact]
    public void ReadDailyNotes_EmptyDirectory_ReturnsEmpty()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"dreaming-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var result = MemoryDreamingCronAction.ReadDailyNotes(tempDir, 14, 50_000);
            result.ShouldBeEmpty();
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ReadDailyNotes_NonExistentDirectory_ReturnsEmpty()
    {
        var result = MemoryDreamingCronAction.ReadDailyNotes("/nonexistent/path", 14, 50_000);
        result.ShouldBeEmpty();
    }

    [Fact]
    public void ReadDailyNotes_ReadsFilesWithinLookbackWindow()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"dreaming-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var today = DateTimeOffset.UtcNow.Date;
            var yesterday = today.AddDays(-1).ToString("yyyy-MM-dd");
            var twoDaysAgo = today.AddDays(-2).ToString("yyyy-MM-dd");
            var tooOld = today.AddDays(-20).ToString("yyyy-MM-dd");

            File.WriteAllText(Path.Combine(tempDir, $"{yesterday}.md"), "Yesterday content");
            File.WriteAllText(Path.Combine(tempDir, $"{twoDaysAgo}.md"), "Two days ago content");
            File.WriteAllText(Path.Combine(tempDir, $"{tooOld}.md"), "Too old content");

            var result = MemoryDreamingCronAction.ReadDailyNotes(tempDir, 14, 50_000);

            result.Count.ShouldBe(2);
            result[0].Date.ShouldBe(yesterday); // newest first
            result[0].Content.ShouldBe("Yesterday content");
            result[1].Date.ShouldBe(twoDaysAgo);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ReadDailyNotes_RespectsMaxContentChars()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"dreaming-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var today = DateTimeOffset.UtcNow.Date;
            var yesterday = today.AddDays(-1).ToString("yyyy-MM-dd");
            var twoDaysAgo = today.AddDays(-2).ToString("yyyy-MM-dd");

            File.WriteAllText(Path.Combine(tempDir, $"{yesterday}.md"), new string('A', 100));
            File.WriteAllText(Path.Combine(tempDir, $"{twoDaysAgo}.md"), new string('B', 100));

            // Cap at 50 chars — only first file should be included (partially)
            var result = MemoryDreamingCronAction.ReadDailyNotes(tempDir, 14, 50);

            result.Count.ShouldBe(1);
            result[0].Content.Length.ShouldBeLessThanOrEqualTo(50 + "[...truncated]\n".Length);
            result[0].Content.ShouldContain("[...truncated]");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ReadDailyNotes_SkipsEmptyFiles()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"dreaming-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var today = DateTimeOffset.UtcNow.Date;
            var yesterday = today.AddDays(-1).ToString("yyyy-MM-dd");
            var twoDaysAgo = today.AddDays(-2).ToString("yyyy-MM-dd");

            File.WriteAllText(Path.Combine(tempDir, $"{yesterday}.md"), "");
            File.WriteAllText(Path.Combine(tempDir, $"{twoDaysAgo}.md"), "Has content");

            var result = MemoryDreamingCronAction.ReadDailyNotes(tempDir, 14, 50_000);

            result.Count.ShouldBe(1);
            result[0].Content.ShouldBe("Has content");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ReadDailyNotes_IgnoresNonDateFiles()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"dreaming-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var today = DateTimeOffset.UtcNow.Date;
            var yesterday = today.AddDays(-1).ToString("yyyy-MM-dd");

            File.WriteAllText(Path.Combine(tempDir, $"{yesterday}.md"), "Valid");
            File.WriteAllText(Path.Combine(tempDir, "not-a-date.md"), "Invalid");
            File.WriteAllText(Path.Combine(tempDir, "MEMORY.md"), "Also not daily");

            var result = MemoryDreamingCronAction.ReadDailyNotes(tempDir, 14, 50_000);

            result.Count.ShouldBe(1);
            result[0].Content.ShouldBe("Valid");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void BuildConsolidationPrompt_ContainsInstructions()
    {
        var notes = new List<(string Date, string Content)>
        {
            ("2026-06-09", "Session started. Implemented feature X."),
            ("2026-06-08", "Fixed bug in cron scheduler.")
        };

        var prompt = MemoryDreamingCronAction.BuildConsolidationPrompt(
            AgentId.From("farnsworth"), notes, "Existing memory content", 14);

        prompt.ShouldContain("Memory Consolidation Task");
        prompt.ShouldContain("farnsworth");
        prompt.ShouldContain("memory_save");
        prompt.ShouldContain("2026-06-09");
        prompt.ShouldContain("Implemented feature X");
        prompt.ShouldContain("Existing memory content");
        prompt.ShouldContain("Do NOT remove existing content");
    }

    [Fact]
    public void BuildConsolidationPrompt_EmptyExistingMemory_OmitsSection()
    {
        var notes = new List<(string Date, string Content)>
        {
            ("2026-06-09", "Some note content")
        };

        var prompt = MemoryDreamingCronAction.BuildConsolidationPrompt(
            AgentId.From("nova"), notes, string.Empty, 7);

        prompt.ShouldNotContain("Current MEMORY.md");
        prompt.ShouldContain("Some note content");
    }

    [Fact]
    public void BuildConsolidationPrompt_TruncatesLargeExistingMemory()
    {
        var notes = new List<(string Date, string Content)>
        {
            ("2026-06-09", "Note")
        };

        var largeMemory = new string('X', 15_000);

        var prompt = MemoryDreamingCronAction.BuildConsolidationPrompt(
            AgentId.From("agent"), notes, largeMemory, 14);

        prompt.ShouldContain("[...truncated]");
    }

    [Fact]
    public void ActionType_ReturnsExpectedValue()
    {
        var action = new MemoryDreamingCronAction();
        action.ActionType.ShouldBe("memory-dreaming");
    }
}
