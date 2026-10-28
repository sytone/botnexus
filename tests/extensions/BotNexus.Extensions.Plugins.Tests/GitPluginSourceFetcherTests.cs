using BotNexus.Extensions.Plugins.Lifecycle;

namespace BotNexus.Extensions.Plugins.Tests;

/// <summary>
/// Pins the git transport's argument construction and its resolved-version contract, without
/// spawning git or touching a network.
/// </summary>
public sealed class GitPluginSourceFetcherTests
{
    private sealed class RecordingGitRunner : IGitCommandRunner
    {
        private readonly Queue<GitCommandResult> _results = new();

        public List<IReadOnlyList<string>> Invocations { get; } = [];

        public void Enqueue(GitCommandResult result) => _results.Enqueue(result);

        public Task<GitCommandResult> RunAsync(
            string workingDirectory,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default)
        {
            Invocations.Add(arguments);
            return Task.FromResult(_results.Count > 0 ? _results.Dequeue() : new GitCommandResult(0, string.Empty, string.Empty));
        }
    }

    [Fact]
    public async Task FetchClonesTheSourceAndReportsTheResolvedCommit()
    {
        var runner = new RecordingGitRunner();
        runner.Enqueue(new GitCommandResult(0, string.Empty, string.Empty));
        runner.Enqueue(new GitCommandResult(0, "0123456789012345678901234567890123456789\n", string.Empty));

        var result = await new GitPluginSourceFetcher(runner)
            .FetchAsync("https://example.com/hello.git", reference: null, stagingDirectory: Path.GetTempPath());

        Assert.Equal("0123456789012345678901234567890123456789", result.ResolvedVersion);
        Assert.Equal(["clone", "--quiet", "--", "https://example.com/hello.git", "."], runner.Invocations[0]);
        Assert.Equal(["rev-parse", "HEAD"], runner.Invocations[1]);
    }

    // The reference is passed through as --branch, and "--" still terminates option parsing so a
    // source beginning with a dash cannot be reinterpreted as a git option.
    [Fact]
    public async Task FetchPassesTheRequestedReferenceAndTerminatesOptionParsing()
    {
        var runner = new RecordingGitRunner();
        runner.Enqueue(new GitCommandResult(0, string.Empty, string.Empty));
        runner.Enqueue(new GitCommandResult(0, "0123456789012345678901234567890123456789", string.Empty));

        await new GitPluginSourceFetcher(runner)
            .FetchAsync("https://example.com/hello.git", "v1.2.0", Path.GetTempPath());

        var clone = runner.Invocations[0];
        Assert.Equal("--branch", clone[2]);
        Assert.Equal("v1.2.0", clone[3]);
        Assert.Equal("--", clone[4]);
    }

    [Fact]
    public async Task FetchThrowsWithSafeGuidanceWhenTheCloneFails()
    {
        var runner = new RecordingGitRunner();
        runner.Enqueue(new GitCommandResult(128, string.Empty, "fatal: repository not found"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new GitPluginSourceFetcher(runner).FetchAsync("https://example.com/nope.git", null, Path.GetTempPath()));

        ex.Message.ShouldBe("Plugin source fetch failed. Check Git availability, connectivity, and repository access in the actual gateway or container environment, then retry. Git diagnostics are withheld because they may contain credentials.");
        ex.ToString().ShouldNotContain("repository not found");
        ex.ToString().ShouldNotContain("https://example.com/nope.git");
        ex.InnerException.ShouldBeNull();
        runner.Invocations.Count.ShouldBe(1);
    }

    // A clone that succeeds but whose revision cannot be resolved must fail, not install content
    // with an empty version - an unversioned record makes update unable to detect movement.
    [Fact]
    public async Task FetchThrowsWhenTheRevisionCannotBeResolved()
    {
        var runner = new RecordingGitRunner();
        runner.Enqueue(new GitCommandResult(0, string.Empty, string.Empty));
        runner.Enqueue(new GitCommandResult(0, "   ", string.Empty));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new GitPluginSourceFetcher(runner).FetchAsync("https://example.com/hello.git", null, Path.GetTempPath()));
    }
}
