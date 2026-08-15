using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace BotNexus.Extensions.GitHub.Tests;

/// <summary>
/// A token source that hands out predictable, distinguishable tokens and counts how many times it
/// was asked to mint. The count is the whole point: it is what distinguishes "cached" from "minted
/// fresh on every call" (#2732 AC2/AC6).
/// </summary>
internal sealed class CountingTokenSource : IGitHubInstallationTokenSource
{
    private readonly Func<int, GitHubInstallationToken> _factory;
    private int _mintCount;

    public CountingTokenSource(Func<int, GitHubInstallationToken> factory) => _factory = factory;

    public int MintCount => _mintCount;

    public Task<GitHubInstallationToken> MintAsync(CancellationToken cancellationToken = default)
    {
        var index = Interlocked.Increment(ref _mintCount);
        return Task.FromResult(_factory(index));
    }
}

/// <summary>A source that always fails, for the sad path.</summary>
internal sealed class ThrowingTokenSource : IGitHubInstallationTokenSource
{
    private readonly Exception _exception;

    public ThrowingTokenSource(Exception exception) => _exception = exception;

    public Task<GitHubInstallationToken> MintAsync(CancellationToken cancellationToken = default) =>
        Task.FromException<GitHubInstallationToken>(_exception);
}

/// <summary>
/// Captures every log line the provider writes, including the formatted message, the raw state
/// key/value pairs, and any exception — so a leak through <i>any</i> of those channels is caught,
/// not just through the rendered message (#2732 AC4).
/// </summary>
internal sealed class CapturingLogger : ILogger<CachedGitHubCredentialProvider>
{
    public ConcurrentBag<string> Lines { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Lines.Add(formatter(state, exception));
        Lines.Add(state?.ToString() ?? string.Empty);

        // Structured values are a separate leak channel from the rendered message: a sink that
        // serialises state would emit them even if the template never interpolated the secret.
        if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
        {
            foreach (var pair in pairs)
            {
                Lines.Add(pair.Key + "=" + pair.Value);
            }
        }

        if (exception is not null)
        {
            Lines.Add(exception.ToString());
        }
    }
}
