namespace BotNexus.Gateway.Configuration.Tests;

public sealed class ConfigParityDiscoveryTests
{
    [Fact]
    public void DiscoverStoreCandidates_BudgetPlusOne_RefusesPartialCandidate()
    {
        var visited = 0;
        IEnumerable<string> Entries()
        {
            visited++;
            yield return ConfigStoreBootstrap.StoreFileName;
            for (var index = 0; index < ConfigStoreParityDiagnostic.MaximumDiscoveryEntries; index++)
            {
                visited++;
                yield return "unrelated-" + index;
            }
            throw new InvalidOperationException("Discovery must not read beyond its bounded refusal.");
        }
        ConfigStoreParityDiagnostic.DiscoverStoreCandidates(Entries(), CancellationToken.None).ShouldBeNull();
        visited.ShouldBe(ConfigStoreParityDiagnostic.MaximumDiscoveryEntries + 1);
    }

    [Fact]
    public void DiscoverStoreCandidates_ExactBudget_KeepsCompleteCandidate()
    {
        var entries = Enumerable.Range(0, ConfigStoreParityDiagnostic.MaximumDiscoveryEntries - 1)
            .Select(index => "unrelated-" + index).Append(ConfigStoreBootstrap.StoreFileName);
        ConfigStoreParityDiagnostic.DiscoverStoreCandidates(entries, CancellationToken.None)
            .ShouldNotBeNull().ShouldHaveSingleItem().ShouldBe(ConfigStoreBootstrap.StoreFileName);
    }

    [Fact]
    public void DiscoverStoreCandidates_PreCancelled_DoesNotEnumerate()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        IEnumerable<string> Entries()
        {
            if (cancellation.IsCancellationRequested)
                throw new InvalidOperationException("Cancelled discovery must not start enumerating.");
            yield return "unrelated";
        }
        Should.Throw<OperationCanceledException>(() => ConfigStoreParityDiagnostic.DiscoverStoreCandidates(Entries(), cancellation.Token));
    }

    [Fact]
    public void DiscoverStoreCandidates_CancelledBetweenEntries_PropagatesBeforePartialSuccess()
    {
        using var cancellation = new CancellationTokenSource();
        var visited = 0;
        IEnumerable<string> Entries()
        {
            visited++;
            yield return ConfigStoreBootstrap.StoreFileName;
            cancellation.Cancel();
            visited++;
            yield return "unrelated";
            throw new InvalidOperationException("Cancelled discovery must not continue.");
        }
        Should.Throw<OperationCanceledException>(() => ConfigStoreParityDiagnostic.DiscoverStoreCandidates(Entries(), cancellation.Token));
        visited.ShouldBe(2);
    }

    [Fact]
    public void DiscoverStoreCandidates_CaseInsensitiveCanonicalAndLegacy_AmbiguityRetained()
    {
        var legacy = Path.ChangeExtension(ConfigStoreBootstrap.StoreFileName, ".db");
        ConfigStoreParityDiagnostic.DiscoverStoreCandidates(
            ["unrelated", ConfigStoreBootstrap.StoreFileName.ToUpperInvariant(), legacy.ToUpperInvariant()], CancellationToken.None)
            .ShouldNotBeNull().Length.ShouldBe(2);
    }
}
