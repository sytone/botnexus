namespace BotNexus.Extensions.BrowserTools;

/// <summary>
/// The page-interaction half of the browser surface (#3031).
/// </summary>
/// <remarks>
/// Split from <see cref="IBrowserDriver"/> on purpose. <see cref="IBrowserDriver"/> is what
/// <see cref="GuardedBrowserSession"/> owns, and everything on it takes or returns a URL, so the
/// guards can reason about it. The three members here act on the page the guards already
/// admitted and carry no navigable target, so routing them through a URL guard would be a check
/// with nothing to check. Keeping them on a separate interface stops that distinction from
/// blurring the moment someone adds a fourth method.
/// </remarks>
public interface IBrowserInteractionDriver
{
    /// <summary>Clicks the element matching <paramref name="selector"/>.</summary>
    Task<string> ClickAsync(string selector, CancellationToken cancellationToken = default);

    /// <summary>Types <paramref name="text"/> into the element matching <paramref name="selector"/>.</summary>
    Task<string> TypeAsync(
        string selector,
        string text,
        bool submit = false,
        CancellationToken cancellationToken = default);

    /// <summary>Writes a screenshot to <paramref name="destinationPath"/>.</summary>
    Task<string> ScreenshotAsync(
        string destinationPath,
        bool fullPage = false,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Everything the five browser tools share for one agent session (#3031).
/// </summary>
/// <remarks>
/// The tools hold this rather than a driver. <see cref="Guarded"/> is the ONLY route to
/// navigation and snapshot, and the raw driver is not reachable from a tool at all, which is what
/// makes AC8 a structural property instead of a convention the next tool might not follow.
/// </remarks>
public sealed class BrowserToolSession : IAsyncDisposable
{
    private readonly IAsyncDisposable? _owned;

    /// <summary>Creates a session.</summary>
    /// <param name="guarded">The guarded navigation/snapshot entry point.</param>
    /// <param name="interaction">The page-interaction driver.</param>
    /// <param name="workspacePath">Absolute agent workspace root; screenshots land beneath it.</param>
    /// <param name="fileSystem">Filesystem seam used to create the screenshot directory.</param>
    /// <param name="owned">Resource disposed with this session, typically the CLI driver.</param>
    public BrowserToolSession(
        GuardedBrowserSession guarded,
        IBrowserInteractionDriver interaction,
        string workspacePath,
        IBrowserFileSystem? fileSystem = null,
        IAsyncDisposable? owned = null)
    {
        Guarded = guarded ?? throw new ArgumentNullException(nameof(guarded));
        Interaction = interaction ?? throw new ArgumentNullException(nameof(interaction));
        WorkspacePath = workspacePath ?? throw new ArgumentNullException(nameof(workspacePath));
        FileSystem = fileSystem ?? new BrowserFileSystem();
        _owned = owned;
    }

    /// <summary>The guarded navigation and snapshot surface.</summary>
    public GuardedBrowserSession Guarded { get; }

    /// <summary>The click/type/screenshot surface.</summary>
    public IBrowserInteractionDriver Interaction { get; }

    /// <summary>Absolute agent workspace root.</summary>
    public string WorkspacePath { get; }

    /// <summary>Filesystem seam.</summary>
    public IBrowserFileSystem FileSystem { get; }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_owned is not null)
        {
            await _owned.DisposeAsync().ConfigureAwait(false);
        }
    }
}
