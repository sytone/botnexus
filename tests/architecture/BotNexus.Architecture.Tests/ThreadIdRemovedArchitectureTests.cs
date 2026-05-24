using System.Text.RegularExpressions;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness functions enforcing the PR #512 invariant:
/// the <c>ThreadId</c> primitive and the <c>thread_id</c> SQLite column have
/// been deleted in favour of composite <c>ChannelAddress</c> values that
/// channel adapters encode themselves (e.g. Telegram folds
/// <c>message_thread_id</c> into the address as <c>/topic:&lt;value&gt;</c>).
/// </summary>
/// <remarks>
/// <para>
/// These fences exist because the original dual model — a separate
/// <c>ThreadId</c> identifier carried alongside <c>ChannelAddress</c> through
/// every contract — caused three distinct "thread-binding hack" workarounds
/// across <c>Messages.cs</c>, <c>DefaultConversationRouter.cs</c> and
/// <c>SqliteConversationStore.cs</c>. Any reintroduction of the type or column
/// is almost certainly an accidental revival of that anti-pattern.
/// </para>
/// <para>
/// The migration block inside <see cref="SqliteConversationStore"/> is
/// allowlisted because it must reference the legacy column name to detect and
/// rewrite pre-#512 rows. New references anywhere else fail the build.
/// </para>
/// </remarks>
public sealed class ThreadIdRemovedArchitectureTests
{
    // Files allowed to mention the historic ThreadId type / thread_id column. The
    // migration code in SqliteConversationStore must reference the legacy column to
    // detect and rewrite pre-#512 rows. TelegramChannelAddress documents on its
    // decoder why non-numeric topic segments are tolerated — that explanation cites
    // the legacy REST `thread_id` field shape, so the comment is the natural place
    // for the reference.
    private static readonly string[] AllowedFiles =
    {
        "SqliteConversationStore.cs",
        "TelegramChannelAddress.cs",
    };

    // Telegram's HTTP API genuinely names its field "message_thread_id" and a few
    // surrounding identifiers (e.g. "MessageThreadId" on Telegram models) reference
    // it. The fence below distinguishes between the deleted BotNexus type
    // "ThreadId" and the Telegram API name "MessageThreadId" by using a word
    // boundary that excludes the "Message" prefix.

    /// <summary>
    /// No file under <c>src/</c> may reference the deleted <c>ThreadId</c> type
    /// outside the allowlist. The migration code in <c>SqliteConversationStore</c>
    /// is allowed to mention the historic name in its rewrite comments.
    /// </summary>
    [Fact]
    public void NoCode_References_ThreadId_Type()
    {
        var srcRoot = FindSourceRoot();
        // Match identifier "ThreadId" not preceded by "Message" (which would be
        // Telegram's API field MessageThreadId — legitimate).
        var pattern = new Regex(@"(?<!Message)\bThreadId\b", RegexOptions.Compiled);

        var offenders = Directory
            .EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
            .Where(IsProductionSource)
            .Where(path => !AllowedFiles.Contains(Path.GetFileName(path), StringComparer.Ordinal))
            .Where(path => pattern.IsMatch(File.ReadAllText(path)))
            .Select(path => Path.GetRelativePath(srcRoot, path))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

        offenders.ShouldBeEmpty(
            "Files under src/ reference the BotNexus ThreadId type — it was deleted in PR #512 " +
            "in favour of composite ChannelAddress encoding owned by the originating channel " +
            "adapter (Telegram: TelegramChannelAddress.Encode/TryDecode). If you need to address " +
            "a native sub-channel (forum topic, thread, etc.), fold the identifier into the " +
            "ChannelAddress at the adapter boundary.\n" +
            "Offenders:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// No file under <c>src/</c> may reference the legacy <c>thread_id</c>
    /// SQLite column outside the migration block in <c>SqliteConversationStore</c>.
    /// </summary>
    [Fact]
    public void NoCode_References_thread_id_Column()
    {
        var srcRoot = FindSourceRoot();

        var offenders = Directory
            .EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
            .Where(IsProductionSource)
            .Where(path => !AllowedFiles.Contains(Path.GetFileName(path), StringComparer.Ordinal))
            .Where(path => File.ReadAllText(path).Contains("thread_id", StringComparison.OrdinalIgnoreCase))
            .Where(path => !IsTelegramApiField(path))
            .Select(path => Path.GetRelativePath(srcRoot, path))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

        offenders.ShouldBeEmpty(
            "Files under src/ reference the legacy thread_id SQLite column — it was dropped in " +
            "PR #512 by the SqliteConversationStore migration. Only the migration code itself " +
            "may reference the column name.\n" +
            "Offenders:\n  " + string.Join("\n  ", offenders));
    }

    private static bool IsProductionSource(string path)
        => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    // Telegram's Bot API genuinely names its JSON field "message_thread_id". Files
    // under the Telegram extension that only reference that exact phrase (never
    // the bare "thread_id" column) are legitimate API consumers, not column references.
    private static bool IsTelegramApiField(string path)
    {
        if (!path.Contains("Channels.Telegram", StringComparison.Ordinal))
            return false;

        var content = File.ReadAllText(path);
        // Strip every occurrence of the Telegram API field name, then check whether
        // any bare "thread_id" mentions remain. If not, this is a Telegram-API-only
        // reference and is allowed.
        var stripped = content.Replace("message_thread_id", string.Empty, StringComparison.OrdinalIgnoreCase);
        return !stripped.Contains("thread_id", StringComparison.OrdinalIgnoreCase);
    }

    private static string FindSourceRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "BotNexus.slnx")))
        {
            current = current.Parent;
        }

        current.ShouldNotBeNull("Could not locate repo root from " + AppContext.BaseDirectory);
        var srcRoot = Path.Combine(current.FullName, "src");
        Directory.Exists(srcRoot).ShouldBeTrue("Expected src/ under " + current.FullName);
        return srcRoot;
    }
}
