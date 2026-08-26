using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration.Store;

namespace BotNexus.Gateway.Configuration.Writers;

/// <summary>
/// Writes a configuration document to every registered backend (#3527).
/// </summary>
/// <remarks>
/// <para>
/// <b>Every writer receives the document, even after one fails.</b> Stopping at the first failure
/// would leave the remaining stores holding the previous document AND hide which ones, so the
/// operator could not tell how far the write got. Attempting all of them means the divergence is at
/// least bounded and reportable.
/// </para>
/// <para>
/// <b>A partial write throws.</b> It is tempting to treat "the file succeeded, only the store failed"
/// as a warning, because the JSON copy is still durable. That is precisely the failure this design
/// exists to prevent: the store wins on read, so a successful-looking write whose store leg failed
/// means the operator's change is invisible while the UI reports success. A loud failure naming the
/// backend is the only honest outcome.
/// </para>
/// </remarks>
public sealed class FanOutConfigurationWriter : IConfigurationWriter
{
    private readonly IReadOnlyList<IConfigurationWriter> _writers;

    /// <summary>Creates a fan-out over <paramref name="writers"/>.</summary>
    /// <param name="writers">
    /// The registered backends, in registration order. An empty set is refused: silently writing
    /// nowhere is worse than failing to start.
    /// </param>
    public FanOutConfigurationWriter(IEnumerable<IConfigurationWriter> writers)
    {
        ArgumentNullException.ThrowIfNull(writers);

        _writers = writers.ToList();
        if (_writers.Count == 0)
        {
            throw new ArgumentException(
                "At least one configuration writer must be registered. A fan-out over zero backends " +
                "would accept every write and persist nothing.",
                nameof(writers));
        }
    }

    /// <inheritdoc />
    public string Name => string.Join('+', _writers.Select(w => w.Name));

    /// <inheritdoc />
    public async Task WriteAsync(JsonObject document, string reason, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        List<Exception>? failures = null;
        var succeeded = new List<string>(_writers.Count);

        foreach (var writer in _writers)
        {
            try
            {
                await writer.WriteAsync(document, reason, cancellationToken).ConfigureAwait(false);
                succeeded.Add(writer.Name);
            }
            catch (OperationCanceledException)
            {
                // Cancellation is the caller's decision, not a backend failure. Propagate it
                // immediately rather than reporting it as a partial write.
                throw;
            }
            catch (Exception ex)
            {
                (failures ??= []).Add(
                    new InvalidOperationException(
                        $"Configuration writer '{writer.Name}' failed to persist the document.", ex));
            }
        }

        if (failures is null)
            return;

        var wrote = succeeded.Count == 0 ? "none" : string.Join(", ", succeeded);
        throw new AggregateException(
            $"Configuration write partially failed. Persisted to: {wrote}. " +
            "The stores now hold different documents; the configuration a reader sees depends on " +
            "provider precedence, so this must not be treated as a successful write.",
            failures);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Every backend receives the same pre-computed change set. The caller already decided which keys
    /// move, so there is nothing for a backend to re-derive from its own state. Failure handling matches
    /// <see cref="WriteAsync"/> - all backends are attempted, and a partial application throws naming
    /// the ones that succeeded.
    /// </remarks>
    public async Task ApplyChangeSetAsync(
        ConfigChangeSet changes,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changes);

        List<Exception>? failures = null;
        var succeeded = new List<string>(_writers.Count);

        foreach (var writer in _writers)
        {
            try
            {
                await writer.ApplyChangeSetAsync(changes, reason, cancellationToken).ConfigureAwait(false);
                succeeded.Add(writer.Name);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                (failures ??= []).Add(
                    new InvalidOperationException(
                        $"Configuration writer '{writer.Name}' failed to apply the change set.", ex));
            }
        }

        if (failures is null)
        {
            return;
        }

        var wrote = succeeded.Count == 0 ? "none" : string.Join(", ", succeeded);
        throw new AggregateException(
            $"Configuration change set partially failed. Applied to: {wrote}. " +
            "The stores now disagree; the configuration a reader sees depends on provider precedence, " +
            "so this must not be treated as a successful write.",
            failures);
    }
}
