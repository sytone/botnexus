using System.IO.Abstractions.TestingHelpers;
using BotNexus.Tools;

namespace BotNexus.CodingAgent.Tests.Tools;

/// <summary>
/// Tests for surrogate pair and emoji handling in EditTool fuzzy matching.
/// Regression coverage for issue #1061: NormalizeFuzzyText crashed on files containing
/// emoji or supplementary plane characters because it processed surrogate pairs char-by-char.
/// </summary>
public sealed class EditToolSurrogatePairTests
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), "tools", "edit-surrogate");
    private readonly MockFileSystem _fileSystem = new();
    private readonly EditTool _tool;

    public EditToolSurrogatePairTests()
    {
        _fileSystem.Directory.CreateDirectory(_tempDirectory);
        _tool = new EditTool(_tempDirectory, _fileSystem);
    }

    [Fact]
    public async Task ExecuteAsync_FileContainingEmoji_EditsSuccessfully()
    {
        // Regression: surrogate pairs (emoji) crashed NormalizeFuzzyText with
        // "String contains invalid Unicode code points" (ArgumentException)
        var filePath = Path.Combine(_tempDirectory, "emoji.txt");
        // U+1F52C = microscope emoji, stored as surrogate pair \uD83D\uDD2C
        await _fileSystem.File.WriteAllTextAsync(filePath, "Hello \U0001F52C world");

        await _tool.ExecuteAsync("test-call", new Dictionary<string, object?>
        {
            ["path"] = "emoji.txt",
            ["edits"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["oldText"] = "Hello \U0001F52C world",
                    ["newText"] = "Goodbye \U0001F52C world"
                }
            }
        });

        (await _fileSystem.File.ReadAllTextAsync(filePath)).ShouldBe("Goodbye \U0001F52C world");
    }

    [Fact]
    public async Task ExecuteAsync_MultipleEmojiInFile_FuzzyMatchStillWorks()
    {
        // File has emoji + smart quotes - fuzzy match should handle both
        var filePath = Path.Combine(_tempDirectory, "emoji-fuzzy.txt");
        // U+1F680 = rocket, U+1F389 = party popper, with curly quotes
        await _fileSystem.File.WriteAllTextAsync(filePath, "\U0001F680 Launch \u201Capp\u201D now \U0001F389");

        await _tool.ExecuteAsync("test-call", new Dictionary<string, object?>
        {
            ["path"] = "emoji-fuzzy.txt",
            ["edits"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    // Agent sends straight quotes - fuzzy match should find curly quotes
                    ["oldText"] = "\U0001F680 Launch \"app\" now \U0001F389",
                    ["newText"] = "\U0001F680 Deploy \"app\" now \U0001F389"
                }
            }
        });

        (await _fileSystem.File.ReadAllTextAsync(filePath)).ShouldContain("Deploy");
    }

    [Fact]
    public async Task ExecuteAsync_EmojiAdjacentToEditTarget_DoesNotCrash()
    {
        // Edit target is sandwiched between emoji - ensures index mapping stays correct
        var filePath = Path.Combine(_tempDirectory, "emoji-adjacent.txt");
        await _fileSystem.File.WriteAllTextAsync(filePath, "prefix\U0001F52Ctarget\U0001F52Csuffix");

        await _tool.ExecuteAsync("test-call", new Dictionary<string, object?>
        {
            ["path"] = "emoji-adjacent.txt",
            ["edits"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["oldText"] = "target",
                    ["newText"] = "replaced"
                }
            }
        });

        (await _fileSystem.File.ReadAllTextAsync(filePath)).ShouldBe("prefix\U0001F52Creplaced\U0001F52Csuffix");
    }

    [Fact]
    public async Task ExecuteAsync_ReplacementCharInFile_DoesNotCrash()
    {
        // U+FFFD (replacement character) appears in files when .NET reads corrupt UTF-8.
        // The fuzzy matcher should handle it gracefully.
        var filePath = Path.Combine(_tempDirectory, "replacement-char.txt");
        _fileSystem.File.WriteAllText(filePath, "before \uFFFD after");

        await _tool.ExecuteAsync("test-call", new Dictionary<string, object?>
        {
            ["path"] = "replacement-char.txt",
            ["edits"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["oldText"] = "before",
                    ["newText"] = "changed"
                }
            }
        });

        (await _fileSystem.File.ReadAllTextAsync(filePath)).ShouldStartWith("changed");
    }

    [Fact]
    public async Task ExecuteAsync_EditTargetContainsEmoji_ExactMatch()
    {
        // The oldText itself contains emoji - exact match path
        var filePath = Path.Combine(_tempDirectory, "emoji-in-oldtext.txt");
        // U+1F7E2 = green circle, U+1F534 = red circle
        await _fileSystem.File.WriteAllTextAsync(filePath, "Status: \U0001F7E2 Online\nEnd");

        await _tool.ExecuteAsync("test-call", new Dictionary<string, object?>
        {
            ["path"] = "emoji-in-oldtext.txt",
            ["edits"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["oldText"] = "Status: \U0001F7E2 Online",
                    ["newText"] = "Status: \U0001F534 Offline"
                }
            }
        });

        (await _fileSystem.File.ReadAllTextAsync(filePath)).ShouldBe("Status: \U0001F534 Offline\nEnd");
    }

    [Fact]
    public async Task ExecuteAsync_EmojiOnlyLine_FuzzyMatchPreservesIndexing()
    {
        // A line that is entirely emoji - tests that index map stays valid
        var filePath = Path.Combine(_tempDirectory, "emoji-line.txt");
        await _fileSystem.File.WriteAllTextAsync(filePath, "line1\n\U0001F52C\U0001F680\U0001F389\nline3");

        await _tool.ExecuteAsync("test-call", new Dictionary<string, object?>
        {
            ["path"] = "emoji-line.txt",
            ["edits"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["oldText"] = "line3",
                    ["newText"] = "updated"
                }
            }
        });

        (await _fileSystem.File.ReadAllTextAsync(filePath)).ShouldBe("line1\n\U0001F52C\U0001F680\U0001F389\nupdated");
    }
}
