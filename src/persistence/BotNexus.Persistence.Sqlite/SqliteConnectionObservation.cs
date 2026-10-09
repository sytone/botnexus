namespace BotNexus.Persistence.Sqlite;

/// <summary>
/// Immutable, atomically captured lifetime observations for explicitly attached logical SQLite
/// connections in this process. This is not a count of native handles or pooled idle resources.
/// </summary>
public sealed record SqliteConnectionObservation
{
    internal SqliteConnectionObservation(long current, long peak, long opens, long closes,
        long poolingEnabled, long poolingDisabled)
    {
        CurrentObservedOpenConnections = current;
        PeakObservedOpenConnections = peak;
        OpenTransitions = opens;
        CloseTransitions = closes;
        PoolingEnabledObservedOpenConnections = poolingEnabled;
        PoolingDisabledObservedOpenConnections = poolingDisabled;
    }

    /// <summary>Observed logical opens not yet balanced by an observed logical close.</summary>
    public long CurrentObservedOpenConnections { get; }

    /// <summary>Process-lifetime high-water mark of concurrently observed logical opens.</summary>
    public long PeakObservedOpenConnections { get; }

    /// <summary>Cumulative entries into observed Open, including already-open attachment.</summary>
    public long OpenTransitions { get; }

    /// <summary>Cumulative departures from observed Open, including close through disposal.</summary>
    public long CloseTransitions { get; }

    /// <summary>
    /// Observed logical opens whose connection string had Pooling enabled at entry into Open.
    /// This describes configuration, not whether the provider actually pools that database.
    /// </summary>
    public long PoolingEnabledObservedOpenConnections { get; }

    /// <summary>Observed logical opens whose connection string had Pooling disabled at entry into Open.</summary>
    public long PoolingDisabledObservedOpenConnections { get; }
}
