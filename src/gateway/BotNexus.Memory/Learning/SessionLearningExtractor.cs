using BotNexus.Domain.Primitives;
using BotNexus.Memory.Models;
using Microsoft.Extensions.Logging;

namespace BotNexus.Memory.Learning;

/// <summary>
/// Distils durable knowledge out of a finished session's indexed turns and stores it alongside
/// them, as <c>learning</c> rows in the agent's own store.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this closes.</b> <see cref="LearningExtractionPipeline"/>, <see cref="TurnClassifier"/>
/// and <see cref="KnowledgeRouter"/> were built and tested but had exactly one caller: the nightly
/// dreaming cron. That caller keeps only the items which route to a <i>shared</i> store and
/// discards the rest, so an agent's own distilled knowledge was never persisted anywhere. Between
/// dreams, everything an agent knew was either something it had explicitly chosen to
/// <c>memory_save</c>, or a raw transcript row that a later search had to rediscover verbatim.
/// </para>
/// <para>
/// <b>Why this is affordable at session end.</b> Classification is
/// <see cref="TurnClassifier.Classify"/> — a set of compiled regexes over the turn pair, with no
/// model call anywhere in the path. The token cost of the dreaming cron comes from the sub-agent
/// session it dispatches to rewrite <c>MEMORY.md</c>, not from extraction. Running extraction per
/// session therefore costs CPU and a few inserts, which is why it can sit on the indexing path
/// rather than waiting for a nightly batch.
/// </para>
/// <para>
/// <b>Promotion is deliberately not done here.</b> The pipeline is driven with no routing rules, so
/// every item resolves with a null <see cref="ExtractedKnowledge.TargetStore"/> and nothing reaches
/// a shared store on this path. Promotion makes one agent's memory visible to every other agent
/// reading that store — a disclosure event, not a bookkeeping step — and it stays on the slower,
/// deliberate cron path where <see cref="SharedMemoryPromoter"/> can dedupe against what is already
/// there. Session end distils; the nightly pass decides what to share.
/// </para>
/// <para>
/// <b>Trust is inherited, never reset.</b> Each row is stamped with
/// <see cref="ExtractedKnowledge.Provenance"/>, which resolves to the least-trusted contributing
/// source rather than the most common one. A learning row distilled from a quarantined transcript
/// row stays quarantined, so <see cref="MemoryInjectionGate"/> keeps it out of always-on prompt
/// context exactly as it would the row it came from. Distillation is not a laundering step.
/// </para>
/// </remarks>
public static class SessionLearningExtractor
{
    /// <summary>The <c>source_type</c> stamped on rows produced by this extractor.</summary>
    public const string LearningSourceType = "learning";

    /// <summary>
    /// Upper bound on rows examined for one session, so a pathologically long transcript cannot
    /// turn session close into an unbounded scan.
    /// </summary>
    public const int MaxEntriesScanned = 500;

    /// <summary>
    /// Extracts and persists durable knowledge for one finished session.
    /// </summary>
    /// <returns>The number of new learning rows written.</returns>
    public static async Task<int> ExtractAsync(
        IMemoryStore store,
        AgentId agentId,
        SessionId sessionId,
        ILogger logger,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);

        // .Value is unwrapped only here, at the store boundary: IMemoryStore addresses rows by the
        // raw column values it persists, so the typed identifiers stop at the edge rather than
        // leaking through the call chain (#3099).
        var sessionKey = sessionId.Value;
        if (string.IsNullOrWhiteSpace(sessionKey))
            return 0;

        var entries = await store.GetBySessionAsync(sessionKey, MaxEntriesScanned, ct).ConfigureAwait(false);
        if (entries.Count == 0)
            return 0;

        // Idempotency, mirroring how IndexSessionCoreAsync dedupes raw turns: a learning row
        // records the turn index it was distilled from, so a second indexing pass over the same
        // session (a reconnect, a replayed close event) recognises the turns it already covered
        // and adds nothing. Without this, every re-index would duplicate the whole distillation.
        var alreadyExtracted = entries
            .Where(entry => entry.SourceType == LearningSourceType && entry.TurnIndex.HasValue)
            .Select(entry => entry.TurnIndex!.Value)
            .ToHashSet();

        // Per-person attribution is inherited from the transcript rows rather than re-derived: the
        // extractor runs after session close and has no caller context of its own. Taking the
        // single distinct value, and nothing when the session's rows disagree, keeps a distilled
        // row from being attributed to someone who did not say it.
        var contributingUsers = entries
            .Where(entry => entry.SourceType == "conversation" && !string.IsNullOrWhiteSpace(entry.UserId))
            .Select(entry => entry.UserId!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var userId = contributingUsers.Count == 1 ? contributingUsers[0] : null;

        // No routing rules: see the class remarks — promotion to shared stores stays on the cron.
        var pipeline = new LearningExtractionPipeline([], logger);
        var extracted = await pipeline.ExtractAsync(entries, ct).ConfigureAwait(false);

        var written = 0;
        foreach (var item in extracted)
        {
            ct.ThrowIfCancellationRequested();

            if (alreadyExtracted.Contains(item.SourceTurnIndex))
                continue;

            var row = new MemoryEntry
            {
                Id = string.Empty,
                AgentId = agentId.Value,
                SessionId = sessionKey,
                TurnIndex = item.SourceTurnIndex,
                SourceType = LearningSourceType,
                Content = item.Content,
                MetadataJson = BuildMetadata(item),
                Embedding = null,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = null,
                ExpiresAt = null,
                IsArchived = false,
                // Least-trusted contributor wins; see the class remarks.
                Provenance = item.Provenance,
                OriginSessionId = sessionKey,
                UserId = userId,
            };

            await store.InsertAsync(row, ct).ConfigureAwait(false);
            alreadyExtracted.Add(item.SourceTurnIndex);
            written++;
        }

        if (written > 0)
        {
            logger.LogInformation(
                "Session learning extraction wrote {Written} learning row(s) for agent '{AgentId}' session '{SessionId}'.",
                written,
                agentId.Value,
                sessionKey);
        }

        return written;
    }

    /// <summary>
    /// Records the classifier's own outputs on the row, so a later reader can see why this was kept
    /// and re-tune the thresholds without re-running the classifier over the raw transcript.
    /// </summary>
    private static string BuildMetadata(ExtractedKnowledge item)
    {
        var category = item.Category.ToString().ToLowerInvariant();
        var confidence = item.Confidence.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        // Tagged with the category as well so the existing tag filter in MemorySearchFilter can
        // select a category without needing a new column or a new filter predicate.
        return $$"""{"category":"{{category}}","confidence":{{confidence}},"tags":["learning","{{category}}"]}""";
    }
}
