namespace BotNexus.Integration.Cli.Tests;

/// <summary>Creates a disposable tag-free Git remote at the current checkout commit.</summary>
internal static class TagFreeLocalSourceRepository
{
    internal static async Task<string> CreateAsync(string sandbox, string sourceRepo, TimeSpan timeout)
    {
        var localRepo = Path.Combine(sandbox, "local-source.git");
        var init = await ProcessRunner.RunAsync("git", $"init --bare \"{localRepo}\"", timeout: timeout);
        init.ExitCode.ShouldBe(0, init.Combined);

        // Fetch only HEAD into this fixture-owned bare repository. No release tags or
        // ambient main branch are required in the CI checkout, and sourceRepo is never mutated.
        var fetch = await ProcessRunner.RunAsync(
            "git", $"-C \"{localRepo}\" fetch --update-shallow --no-tags \"{sourceRepo}\" HEAD:refs/heads/main", timeout: timeout);
        fetch.ExitCode.ShouldBe(0, fetch.Combined);

        // A shallow GitHub Actions checkout can cause git fetch to reject updating main
        // with a warning while still returning exit code zero. Prove the ref exists instead
        // of passing an empty remote to install --latest.
        var main = await ProcessRunner.RunAsync(
            "git", $"-C \"{localRepo}\" rev-parse --verify refs/heads/main^{{commit}}", timeout: timeout);
        main.ExitCode.ShouldBe(0, $"The fixture remote did not publish main.\nFetch: {fetch.Combined}\nRef: {main.Combined}");
        return localRepo;
    }
}
