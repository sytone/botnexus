using BotNexus.Extensions.BrowserTools;

namespace BotNexus.Extensions.BrowserTools.Tests;

/// <summary>
/// The only driver any test in this project sees (#3030 AC7).
/// </summary>
/// <remarks>
/// <para>
/// It launches nothing and opens no socket. That is not merely convenient - it is the assertion
/// mechanism for AC1. Every guard denial is required to happen BEFORE the driver is reached, so
/// <see cref="NavigateCalls"/> being empty after a denied navigation is direct evidence that no
/// subprocess could have been launched, rather than a claim about code paths.
/// </para>
/// <para>
/// Reads throw by default. A test that reaches a driver read it did not arrange is a test whose
/// guard let something through, and a silently-empty default would hide exactly that.
/// </para>
/// </remarks>
internal sealed class FakeBrowserDriver : IBrowserDriver
{
    private readonly Queue<string> _currentUrls = new();

    /// <summary>Every URL <see cref="NavigateAsync"/> was actually asked to load.</summary>
    public List<string> NavigateCalls { get; } = [];

    /// <summary>Number of times the page text was read.</summary>
    public int PageTextReads { get; private set; }

    /// <summary>Page text handed back by <see cref="GetPageTextAsync"/>.</summary>
    public string PageText { get; set; } = "hello";

    /// <summary>
    /// Queues the values <see cref="GetCurrentUrlAsync"/> returns, in order. Queueing rather than
    /// a single value is what lets a test model a page that rewrites its own location after load.
    /// </summary>
    public void QueueCurrentUrl(params string[] urls)
    {
        foreach (var url in urls)
        {
            _currentUrls.Enqueue(url);
        }
    }

    public Task NavigateAsync(string url, CancellationToken cancellationToken = default)
    {
        NavigateCalls.Add(url);
        return Task.CompletedTask;
    }

    public Task<string> GetCurrentUrlAsync(CancellationToken cancellationToken = default)
    {
        if (_currentUrls.Count == 0)
        {
            throw new InvalidOperationException(
                "GetCurrentUrlAsync was called without a queued URL. A guard admitted a snapshot "
                + "the test did not arrange for.");
        }

        return Task.FromResult(_currentUrls.Dequeue());
    }

    public Task<string> GetPageTextAsync(CancellationToken cancellationToken = default)
    {
        PageTextReads++;
        return Task.FromResult(PageText);
    }
}
