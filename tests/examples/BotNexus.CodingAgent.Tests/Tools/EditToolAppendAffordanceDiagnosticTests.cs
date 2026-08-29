using BotNexus.Tools;
using System.IO.Abstractions.TestingHelpers;

namespace BotNexus.CodingAgent.Tests.Tools;

/// <summary>
/// Clause 5 of issue #3571: an <c>edit</c> whose <c>oldText</c> is an exact prefix of its
/// <c>newText</c> and matches more than once is a faked append, and the ambiguity error must name the
/// real affordance instead of only telling the caller to widen an anchor it should not need. Issues
/// #1555 and #1736 established the self-correction pattern these extend; the existing "found N" and
/// "Matches at lines" text is asserted alongside so this cannot be satisfied by replacing it.
/// </summary>
public sealed class EditToolAppendAffordanceDiagnosticTests
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), "tools", "edit-append-affordance");
    private readonly MockFileSystem _fileSystem = new();
    private readonly EditTool _tool;

    public EditToolAppendAffordanceDiagnosticTests()
    {
        _fileSystem.Directory.CreateDirectory(_tempDirectory);
        _tool = new EditTool(_tempDirectory, _fileSystem);
    }

    private async Task<InvalidOperationException> RunInvalidAsync(
        string fileName,
        string fileContent,
        string oldText,
        string newText)
    {
        var filePath = Path.Combine(_tempDirectory, fileName);
        await _fileSystem.File.WriteAllTextAsync(filePath, fileContent);

        var action = () => _tool.ExecuteAsync("test-call", new Dictionary<string, object?>
        {
            ["path"] = fileName,
            ["edits"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["oldText"] = oldText,
                    ["newText"] = newText
                }
            }
        });

        return await action.ShouldThrowAsync<InvalidOperationException>();
    }

    /// <summary>
    /// Clause 5 and clause 6: the corpus worst case — an anchor line repeated eleven times in a daily
    /// log, with the agent reproducing it verbatim at the head of newText. The message must still carry
    /// the accurate "found 11" diagnostic AND name the append affordance.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WhenFakedAppendAnchorMatchesElevenTimes_NamesTheAppendAffordance()
    {
        const string anchor = "**Noise candidates:** none new.";
        var content = string.Join("\n", Enumerable.Range(0, 11).Select(i => $"## Run {i}\n{anchor}"));

        var exception = await RunInvalidAsync(
            "log.md",
            content,
            anchor,
            anchor + "\n\n## Run 11 - Inbox noise cleanup\nnew entry");

        exception.Message.ShouldContain("found 11");
        exception.Message.ShouldContain("Matches at lines");
        exception.Message.ShouldContain("append");
        exception.Message.ShouldContain("`write`");
    }

    /// <summary>The same must hold at the smallest ambiguous count, not just the dramatic one.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenFakedAppendAnchorMatchesTwice_NamesTheAppendAffordance()
    {
        const string anchor = "**Result:** No unprocessed thread.";

        var exception = await RunInvalidAsync(
            "two.md",
            $"{anchor}\nmiddle\n{anchor}\n",
            anchor,
            anchor + "\nnew tail");

        exception.Message.ShouldContain("found 2");
        exception.Message.ShouldContain("append");
    }

    /// <summary>
    /// Non-vacuity guard: an ordinary ambiguous replace — where oldText is NOT a prefix of newText — must
    /// NOT mention append, or the hint is noise on every ambiguity error rather than a diagnosis.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WhenAmbiguousButNotAnAppend_DoesNotMentionTheAppendAffordance()
    {
        var exception = await RunInvalidAsync(
            "plain.md",
            "value = 1\nother\nvalue = 1\n",
            "value = 1",
            "value = 2");

        exception.Message.ShouldContain("found 2");
        exception.Message.ShouldContain("Add surrounding context to oldText");
        exception.Message.ShouldNotContain("append");
    }

    /// <summary>
    /// A prefix-shaped edit that matches exactly once is a legitimate anchored insert and must still
    /// succeed untouched — the affordance hint belongs only on the failure path.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WhenPrefixShapedEditMatchesOnce_StillSucceeds()
    {
        const string anchor = "unique anchor line";
        var filePath = Path.Combine(_tempDirectory, "unique.md");
        await _fileSystem.File.WriteAllTextAsync(filePath, $"header\n{anchor}\n");

        await _tool.ExecuteAsync("test-call", new Dictionary<string, object?>
        {
            ["path"] = "unique.md",
            ["edits"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["oldText"] = anchor,
                    ["newText"] = anchor + "\nappended"
                }
            }
        });

        (await _fileSystem.File.ReadAllTextAsync(filePath)).ShouldContain($"{anchor}\nappended");
    }
}
