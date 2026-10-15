using System.CommandLine;
using System.Text;
using System.Text.Json;
using BotNexus.Cli.Commands;
using Spectre.Console;

namespace BotNexus.Cli.Tests;

/// <summary>
/// Pins offline log inspection against active writers and individually unreadable files (#4859).
/// All contention is established synchronously with .NET file handles, not permissions or sleeps.
/// </summary>
[Collection("AnsiConsole")]
public sealed class DebugLogsSharedReadTests : IDisposable
{
    private const string OlderFileName = "botnexus-2026061014.log";
    private const string ActiveFileName = "botnexus-2026061015.log";
    private const string Session = "sess-shared-read";
    private const string OlderLine = "2026-06-10 14:00:00.000 +00:00 [ERR] sess-shared-read older error";
    private const string ActiveInfo = "2026-06-10 15:00:00.000 +00:00 [INF] sess-shared-read active info";
    private const string ActiveError = "2026-06-10 15:01:00.000 +00:00 [ERR] sess-shared-read active error";
    private const string AppendedLine = "2026-06-10 15:02:00.000 +00:00 [ERR] sess-shared-read appended error";
    private readonly string _home = Path.Combine(Path.GetTempPath(), $"bn-logshare-{Guid.NewGuid():N}");
    private readonly string _logsDir;

    public DebugLogsSharedReadTests()
    {
        _logsDir = Path.Combine(_home, "logs");
        Directory.CreateDirectory(_logsDir);
        File.WriteAllLines(Path.Combine(_logsDir, OlderFileName), [OlderLine]);
        File.WriteAllLines(Path.Combine(_logsDir, ActiveFileName), [ActiveInfo, ActiveError]);
    }

    public void Dispose()
    {
        Directory.Delete(_home, recursive: true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadLines_ActiveWriteOnlyHandle_ReadsWithoutChangingFileAndAllowsAppend(bool search)
    {
        var activePath = Path.Combine(_logsDir, ActiveFileName);
        var originalBytes = File.ReadAllBytes(activePath);
        using var writer = OpenActiveWriter(activePath);

        var entries = search
            ? DebugLogsCommand.SearchLines(_logsDir, Session, null, 10)
            : DebugLogsCommand.TailLines(_logsDir, 10, null);

        entries.Select(entry => entry.Message).ShouldBe(new[]
        {
            $"{Session} older error", $"{Session} active info", $"{Session} active error"
        });
        entries.Select(entry => entry.SourceFile).ShouldBe(new[]
        {
            OlderFileName, ActiveFileName, ActiveFileName
        });
        ReadSharedBytes(activePath).ShouldBe(originalBytes);

        Append(writer);
        // Reopening a writer also detects a leaked reader that denies write sharing on Windows.
        using (var secondWriter = OpenActiveWriter(activePath))
        {
            secondWriter.Length.ShouldBeGreaterThan(originalBytes.Length);
        }
        var afterAppend = search
            ? DebugLogsCommand.SearchLines(_logsDir, Session, null, 10)
            : DebugLogsCommand.TailLines(_logsDir, 10, null);
        afterAppend.Count.ShouldBe(4);
        afterAppend[^1].Message.ShouldBe($"{Session} appended error");
    }

    [Theory]
    [InlineData("tail", 3)]
    [InlineData("errors", 2)]
    [InlineData("search", 3)]
    [InlineData("session", 3)]
    public async Task Build_ActiveWriter_AllOfflineRoutesReturnJsonAndAllowAppend(string route, int expectedCount)
    {
        using var writer = OpenActiveWriter(Path.Combine(_logsDir, ActiveFileName));
        using var capture = new OutputCapture();

        var exitCode = await InvokeRouteAsync(route);

        exitCode.ShouldBe(0);
        using var json = JsonDocument.Parse(capture.Output.ToString());
        var entries = json.RootElement.EnumerateArray().ToArray();
        entries.Length.ShouldBe(expectedCount);
        entries[^1].GetProperty("message").GetString().ShouldBe($"{Session} active error");
        entries[^1].GetProperty("sourceFile").GetString().ShouldBe(ActiveFileName);
        if (route == "errors")
            entries.ShouldAllBe(entry => entry.GetProperty("level").GetString() == "ERR");
        capture.Error.ToString().ShouldBeEmpty();
        Append(writer);
        writer.Length.ShouldBeGreaterThan(0);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ReadLines_OneExclusivelyLockedFile_KeepsOtherReadableFiles(bool search, bool lockNewest)
    {
        var lockedName = lockNewest ? ActiveFileName : OlderFileName;
        using var locked = new FileStream(Path.Combine(_logsDir, lockedName),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var capture = new OutputCapture();

        var entries = search
            ? DebugLogsCommand.SearchLines(_logsDir, Session, null, 10)
            : DebugLogsCommand.TailLines(_logsDir, 10, null);

        entries.Count.ShouldBe(lockNewest ? 1 : 2);
        entries.ShouldAllBe(entry => entry.SourceFile == (lockNewest ? OlderFileName : ActiveFileName));
        entries[0].Message.ShouldBe(lockNewest ? $"{Session} older error" : $"{Session} active info");
        capture.Output.ToString().ShouldBeEmpty();
        capture.Error.ToString().ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("tail", 3)]
    [InlineData("errors", 2)]
    [InlineData("search", 3)]
    [InlineData("session", 3)]
    public async Task Build_UnreadableNewestFile_KeepsOlderJsonAndWritesSafeBoundedDiagnosticToStderr(
        string route, int expectedCount)
    {
        // Brackets are legal filenames on both platforms but invalid unescaped Spectre markup.
        // Padding exercises bounded diagnostics without requiring a platform-specific ACL.
        var lockedName = "botnexus-2026061016[invalid-style]" + new string('x', 100) + ".log";
        var lockedPath = Path.Combine(_logsDir, lockedName);
        File.WriteAllLines(lockedPath, ["2026-06-10 16:00:00.000 +00:00 [ERR] unreadable sentinel"]);
        using var locked = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var capture = new OutputCapture();

        var exitCode = await InvokeRouteAsync(route);

        exitCode.ShouldBe(0);
        // Parsing the entire stdout payload rejects warnings before or after the JSON array.
        using var json = JsonDocument.Parse(capture.Output.ToString());
        var entries = json.RootElement.EnumerateArray().ToArray();
        entries.Length.ShouldBe(expectedCount);
        entries.ShouldAllBe(entry => entry.GetProperty("sourceFile").GetString() != lockedName);
        entries[0].GetProperty("message").GetString().ShouldBe($"{Session} older error");
        if (route == "errors")
            entries.ShouldAllBe(entry => entry.GetProperty("level").GetString() == "ERR");

        var diagnostic = capture.Error.ToString();
        diagnostic.ShouldNotBeNullOrWhiteSpace();
        diagnostic.Length.ShouldBeLessThanOrEqualTo(1024);
        // Accept raw text or escaped Spectre text, but never a swallowed/misinterpreted filename.
        diagnostic.Replace("[[", "[", StringComparison.Ordinal)
            .Replace("]]", "]", StringComparison.Ordinal).ShouldContain("botnexus-2026061016[invalid-style]");
        diagnostic.ShouldNotContain("\u001b");
        diagnostic.ShouldNotContain("   at ");
        diagnostic.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).ShouldHaveSingleItem();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OpenLogReader_RotationWhileOpen_PreservesOriginalAndAllowsReplacement(bool delete)
    {
        var path = Path.Combine(_logsDir, ActiveFileName);
        using var reader = DebugLogsCommand.OpenLogReader(path);
        reader.BaseStream.CanWrite.ShouldBeFalse();
        if (delete)
            File.Delete(path);
        else
            File.Move(path, path + ".rotated");
        File.WriteAllText(path, AppendedLine + Environment.NewLine);
        reader.ReadToEnd().ShouldContain(ActiveInfo);
        DebugLogsCommand.TailLines(_logsDir, 1, null)[0].Message.ShouldBe($"{Session} appended error");
    }

    [Fact]
    public void OpenLogReader_WriterAppendsWhileReaderOpen_AllowsWriteAndRetainsBytes()
    {
        var path = Path.Combine(_logsDir, ActiveFileName);
        using var reader = DebugLogsCommand.OpenLogReader(path);
        using (var writer = OpenActiveWriter(path))
            Append(writer);
        reader.ReadToEnd().ShouldContain(AppendedLine);
    }

    private static FileStream OpenActiveWriter(string path) => new(
        path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);

    private static byte[] ReadSharedBytes(string path)
    {
        // The assertion's own reader must not reproduce File.ReadAllBytes' restrictive sharing.
        using var reader = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var bytes = new MemoryStream();
        reader.CopyTo(bytes);
        return bytes.ToArray();
    }

    private static void Append(FileStream writer)
    {
        writer.Write(Encoding.UTF8.GetBytes(AppendedLine + Environment.NewLine));
        writer.Flush();
    }

    private Task<int> InvokeRouteAsync(string route)
    {
        // Build only the offline logs command; never construct the gateway or the full CLI root.
        var target = new Option<string?>("--target");
        var root = new RootCommand();
        root.AddGlobalOption(target);
        root.AddCommand(new DebugLogsCommand().Build(target));
        var args = new List<string>
        {
            "--target", _home, "logs", "--format", "json", route
        };
        if (route == "search") args.AddRange(["--term", Session]);
        if (route == "session") args.Add(Session);
        args.AddRange(["--limit", "10"]);
        return root.InvokeAsync(args.ToArray());
    }

    private sealed class OutputCapture : IDisposable
    {
        private readonly IAnsiConsole _originalConsole = AnsiConsole.Console;
        private readonly TextWriter _originalOut = Console.Out;
        private readonly TextWriter _originalError = Console.Error;
        internal StringWriter Output { get; } = new();
        internal StringWriter Error { get; } = new();

        internal OutputCapture()
        {
            Console.SetOut(Output);
            Console.SetError(Error);
            AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
            {
                Out = new AnsiConsoleOutput(Output),
                Ansi = AnsiSupport.No,
                Interactive = InteractionSupport.No
            });
            // Rendering must not wrap JSON or the single-line diagnostic in this fixture.
            AnsiConsole.Console.Profile.Width = 4096;
        }

        public void Dispose()
        {
            AnsiConsole.Console = _originalConsole;
            Console.SetOut(_originalOut);
            Console.SetError(_originalError);
            Output.Dispose();
            Error.Dispose();
        }
    }
}
