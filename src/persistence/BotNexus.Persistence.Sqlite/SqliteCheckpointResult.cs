namespace BotNexus.Persistence.Sqlite;

/// <summary>Outcome reported by SQLite for a WAL checkpoint.</summary>
/// <param name="Busy">Non-zero when the requested checkpoint could not complete because a database connection was busy.</param>
/// <param name="LogFrames">Number of frames remaining in the WAL after the checkpoint.</param>
/// <param name="CheckpointedFrames">Number of WAL frames copied back into the database.</param>
/// <param name="Executed">Whether the checkpoint pragma ran before the connection was disposed.</param>
public sealed record SqliteCheckpointResult(
    int Busy,
    int LogFrames,
    int CheckpointedFrames,
    bool Executed = true)
{
    /// <summary>Whether every reported WAL frame was checkpointed without a busy outcome.</summary>
    public bool ReclamationCompleted => Executed && Busy == 0 && CheckpointedFrames == LogFrames;

    /// <summary>Outcome returned when the connection was disposed before the pragma could run.</summary>
    public static SqliteCheckpointResult NotExecuted { get; } = new(0, 0, 0, Executed: false);
}
