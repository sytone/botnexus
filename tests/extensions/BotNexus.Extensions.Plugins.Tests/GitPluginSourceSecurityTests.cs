using BotNexus.Extensions.Plugins.Lifecycle;

namespace BotNexus.Extensions.Plugins.Tests;

/// <summary>Exercises source admission and diagnostic secrecy without running Git.</summary>
public sealed class GitPluginSourceSecurityTests
{
    private const string Secret = "SYNTHETIC_4151_SECRET";
    private const string Rejected = "Plugin source rejected. Use an HTTPS or SSH Git source without inline credentials, query parameters, or fragments. Local checkout handoff is not supported.";
    private const string Failed = "Plugin source fetch failed. Check Git availability, connectivity, and repository access in the actual gateway or container environment, then retry. Git diagnostics are withheld because they may contain credentials.";

    [Theory]
    [InlineData("https://user:SYNTHETIC_4151_SECRET@example.com/repo.git")]
    [InlineData("https://SYNTHETIC_4151_SECRET@example.com/repo.git")]
    [InlineData("https://example.com/repo.git?token=SYNTHETIC_4151_SECRET")]
    [InlineData("https://example.com/repo.git#SYNTHETIC_4151_SECRET")]
    [InlineData("https://example.com/repo.git?")]
    [InlineData("https://example.com/repo.git#")]
    [InlineData("https://example.com/repo.git\nSYNTHETIC_4151_SECRET")]
    [InlineData("https://example.com/\tSYNTHETIC_4151_SECRET")]
    [InlineData("https://example.com/repo\0.git")]
    [InlineData(" https://example.com/repo.git")]
    [InlineData("https://example.com/repo.git ")]
    [InlineData("https://example.com\\repo.git")]
    [InlineData("https://user%40example.com/repo.git")]
    [InlineData("https://example.com/repo%0a.git")]
    [InlineData("ssh://git:SYNTHETIC_4151_SECRET@example.com/repo.git")]
    [InlineData("git:SYNTHETIC_4151_SECRET@example.com:repo.git")]
    [InlineData("git@example.com:repo.git?token=SYNTHETIC_4151_SECRET")]
    [InlineData("ext::SYNTHETIC_4151_SECRET")]
    [InlineData("file:///SYNTHETIC_4151_SECRET/repo")]
    [InlineData("/SYNTHETIC_4151_SECRET/repo")]
    [InlineData("./SYNTHETIC_4151_SECRET/repo")]
    [InlineData("C:\\SYNTHETIC_4151_SECRET\\repo")]
    [InlineData("C:/SYNTHETIC_4151_SECRET/repo")]
    [InlineData("\\\\server\\SYNTHETIC_4151_SECRET")]
    [InlineData("--upload-pack=SYNTHETIC_4151_SECRET")]
    [InlineData("http://example.com/repo.git")]
    [InlineData("git://example.com/repo.git")]
    [InlineData("https:///repo.git")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task FetchAsync_UnsafeSource_RejectsBeforeGitWithFixedDiagnostic(string source)
    {
        var runner = new Runner();
        var error = await Should.ThrowAsync<InvalidOperationException>(() =>
            new GitPluginSourceFetcher(runner).FetchAsync(source, null, Path.GetTempPath()));
        error.Message.ShouldBe(Rejected);
        error.InnerException.ShouldBeNull();
        error.ToString().ShouldNotContain(Secret);
        runner.Calls.Count.ShouldBe(0);
    }

    [Theory]
    [InlineData("https://example.com/team/repo.git")]
    [InlineData("https://example.com:8443/team/repo.git")]
    [InlineData("ssh://git@example.com/team/repo.git")]
    [InlineData("ssh://git@example.com:2222/team/repo.git")]
    [InlineData("ssh://example.com/team/repo.git")]
    [InlineData("git@example.com:team/repo.git")]
    [InlineData("git@example.com:/team/repo.git")]
    public async Task FetchAsync_SupportedSource_PreservesTransportAndRevision(string source)
    {
        var runner = new Runner();
        var result = await new GitPluginSourceFetcher(runner).FetchAsync(source, "main", Path.GetTempPath());
        result.ResolvedVersion.ShouldBe("0123456789012345678901234567890123456789");
        runner.Calls.Count.ShouldBe(2);
        runner.Calls[0].ShouldBe(["clone", "--quiet", "--branch", "main", "--", source, "."]);
        runner.Calls[1].ShouldBe(["rev-parse", "HEAD"]);
    }

    [Theory]
    [InlineData(0)] // clone failure, both output streams contain secrets
    [InlineData(1)] // revision failure
    [InlineData(2)] // runner exception at clone
    [InlineData(3)] // runner exception at revision
    [InlineData(4)] // empty revision
    [InlineData(5)] // unsolicited cancellation is not evidence of caller cancellation
    public async Task FetchAsync_Failure_DropsSourceOutputAndExceptionChains(int failure)
    {
        var runner = new Runner { Failure = failure };
        var error = await Should.ThrowAsync<InvalidOperationException>(() =>
            new GitPluginSourceFetcher(runner).FetchAsync("https://private.example.com/private-repo.git", null, Path.GetTempPath()));
        error.Message.ShouldBe(Failed);
        error.InnerException.ShouldBeNull();
        error.ToString().ShouldNotContain(Secret);
        error.ToString().ShouldNotContain("private.example.com");
        error.ToString().ShouldNotContain("private-repo");
        runner.Calls.Count.ShouldBe(failure is 0 or 2 or 5 ? 1 : 2);
    }

    [Theory]
    [InlineData("0123456789012345678901234567890123456789\nSYNTHETIC_4151_SECRET")]
    [InlineData("SYNTHETIC_4151_SECRET")]
    [InlineData("abc123")]
    public async Task FetchAsync_SuccessfulRevisionOutput_MustBeOneFullObjectId(string output)
    {
        var runner = new Runner { RevisionOutput = output };
        var error = await Should.ThrowAsync<InvalidOperationException>(() =>
            new GitPluginSourceFetcher(runner).FetchAsync("https://example.com/repo.git", null, Path.GetTempPath()));
        error.Message.ShouldBe(Failed);
        error.ToString().ShouldNotContain(Secret);
        error.InnerException.ShouldBeNull();
    }

    [Fact]
    public async Task FetchAsync_CallerCancellation_PreservesCancellationWithoutLeakingDetails()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var runner = new Runner { Failure = 5 };
        var error = await Should.ThrowAsync<OperationCanceledException>(() =>
            new GitPluginSourceFetcher(runner).FetchAsync("https://example.com/repo.git", null, Path.GetTempPath(), cancellation.Token));
        error.CancellationToken.ShouldBe(cancellation.Token);
        error.InnerException.ShouldBeNull();
        error.ToString().ShouldNotContain(Secret);
    }

    private sealed class Runner : IGitCommandRunner
    {
        public int Failure { get; init; } = -1;
        public string RevisionOutput { get; init; } = "0123456789012345678901234567890123456789";
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public Task<GitCommandResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            Calls.Add(arguments);
            if ((Failure == 2 && Calls.Count == 1) || (Failure == 3 && Calls.Count == 2))
            {
                throw new IOException(Secret, new Exception(Secret));
            }
            if (Failure == 5)
            {
                throw new OperationCanceledException(Secret, new Exception(Secret), cancellationToken);
            }
            if ((Failure == 0 && Calls.Count == 1) || (Failure == 1 && Calls.Count == 2))
            {
                return Task.FromResult(new GitCommandResult(128, Secret, Secret));
            }
            return Task.FromResult(new GitCommandResult(0, Failure == 4 ? " " : RevisionOutput, string.Empty));
        }
    }
}
