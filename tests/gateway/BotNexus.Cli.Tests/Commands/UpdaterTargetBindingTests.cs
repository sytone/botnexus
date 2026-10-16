using BotNexus.Cli.Commands;
using BotNexus.Gateway.Contracts.Updates;
using Xunit;

namespace BotNexus.Cli.Tests.Commands;

public sealed class UpdaterTargetBindingTests
{
    [Theory]
    [InlineData(ReleaseTargetKind.Stable)]
    [InlineData(ReleaseTargetKind.Exact)]
    [InlineData(ReleaseTargetKind.Latest)]
    public async Task ReadTargetContract_WorkingTreeAndRefMove_StillReadsResolvedCommit(ReleaseTargetKind kind)
    {
        var root = Path.Combine(Path.GetTempPath(), "botnexus-compat-binding-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            void Git(params string[] args)
            {
                var result = UpdaterReleasePolicyTests.Run("git", root, args);
                result.Code.ShouldBe(0, result.Output);
            }
            Git("init", "-q", "-b", "main");
            Git("config", "user.name", "BotNexus Test");
            Git("config", "user.email", "botnexus-test@example.invalid");
            Git("config", "commit.gpgsign", "false");
            var contractPath = Path.Combine(root, UpdaterCompatibility.ContractPath);
            var contract = UpdaterContractTests.Contract();
            File.WriteAllText(contractPath, contract);
            Git("add", "--", UpdaterCompatibility.ContractPath);
            Git("commit", "-q", "-m", "fixture contract");
            var commit = UpdaterReleasePolicyTests.Run("git", root, "rev-parse", "HEAD").Output.Trim();
            Git("tag", "v0.50.2");
            var target = new ResolvedReleaseTarget(kind, "0.50.2", "v0.50.2", "fixture", commit);
            File.WriteAllText(contractPath, UpdaterContractTests.Contract(minimum: "9.0.0"));
            Git("add", "--", UpdaterCompatibility.ContractPath);
            Git("commit", "-q", "-m", "move branch away from resolved target");
            Git("tag", "-f", "v0.50.2");
            File.WriteAllText(contractPath, "malformed dirty working tree");
            (await ReleaseTargetGitResolver.ReadTargetFileAsync(root, target, UpdaterCompatibility.ContractPath, CancellationToken.None)).ShouldBe(contract);
            File.ReadAllText(contractPath).ShouldBe("malformed dirty working tree");
            (await ReleaseTargetGitResolver.ReadTargetFileAsync(root, target, "missing.json", CancellationToken.None)).ShouldBeNull();
        }
        finally
        {
            // Git object files are read-only on Windows; clear attributes before cleanup.
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, true);
        }
    }
}
