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
            "git", $"-C \"{localRepo}\" fetch --no-tags \"{sourceRepo}\" HEAD:refs/heads/main", timeout: timeout);
        fetch.ExitCode.ShouldBe(0, fetch.Combined);
        return localRepo;
    }
}
