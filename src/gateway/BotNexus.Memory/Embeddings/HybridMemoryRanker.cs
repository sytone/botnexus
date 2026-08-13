using BotNexus.Memory.Models;

namespace BotNexus.Memory.Embeddings;

/// <summary>
/// A single ranking candidate: a memory row plus the signals available for it.
/// </summary>
/// <param name="Entry">The memory row.</param>
/// <param name="LexicalScore">
/// Non-negative lexical relevance (BM25 magnitude, or term-hit count on the LIKE fallback).
/// Zero for a row that was reached only through vector similarity.
/// </param>
/// <param name="Similarity">
/// Cosine similarity in <c>[-1, 1]</c>, or <see langword="null"/> when no comparable vector
/// exists — because embeddings are disabled, the row has none, or its identity differs.
/// </param>
/// <param name="AgeDays">Age of the row in days, used for temporal decay.</param>
public sealed record MemoryRankingCandidate(MemoryEntry Entry, double LexicalScore, double? Similarity, double AgeDays);

/// <summary>
/// A ranked memory row paired with the fused relevance magnitude that placed it there.
/// </summary>
/// <param name="Entry">The memory row.</param>
/// <param name="Score">
/// The fused relevance score produced by <see cref="HybridMemoryRanker"/>. Higher is more relevant.
/// This is the single definition of relevance: callers render and threshold this value rather than
/// deriving a second notion of relevance downstream (#2781).
/// </param>
public sealed record ScoredMemoryEntry(MemoryEntry Entry, double Score);

/// <summary>
/// Fuses lexical relevance and vector similarity into one ordering while preserving the
/// existing temporal-decay behaviour.
/// </summary>
/// <remarks>
/// Two properties matter more than the exact weights:
/// <list type="number">
/// <item><description>
/// When no candidate carries a similarity — embeddings disabled, model unavailable, or every
/// stored vector belongs to a different identity — the ranking collapses to exactly the
/// pre-existing <c>lexical * exp(-lambda * age)</c> formula. Degradation is not a
/// near-equivalent path; it is the original path.
/// </description></item>
/// <item><description>
/// Lexical normalisation is scale-only (divide by the maximum) rather than min-max. An
/// affine shift would silently reorder rows once decay multiplies through, so scale
/// normalisation is what makes the guarantee above hold.
/// </description></item>
/// </list>
/// A row that has no comparable vector inside an otherwise vector-enabled result set is
/// given a neutral similarity prior rather than being scored as dissimilar. Treating "no
/// evidence" as evidence of dissimilarity would bury every not-yet-embedded row the moment
/// embeddings were switched on - including exact lexical matches - which is precisely the
/// regression this feature must not cause.
/// </remarks>
public static class HybridMemoryRanker
{
    /// <summary>Weight applied to the normalised lexical signal.</summary>
    public const double LexicalWeight = 0.6d;

    /// <summary>Weight applied to the normalised similarity signal.</summary>
    public const double SimilarityWeight = 0.4d;

    /// <summary>
    /// Normalised similarity assigned to a row that carries no comparable vector. Sits at the
    /// midpoint of the normalised range so such a row is neither rewarded nor penalised for
    /// the absence of an embedding.
    /// </summary>
    private const double NeutralSimilarityPrior = 0.5d;

    /// <summary>
    /// Jaccard token-overlap above which two rows are treated as near-duplicates and collapsed to a
    /// single representative (#2782).
    /// </summary>
    /// <remarks>
    /// The value is set from the shape of the defect rather than tuned for aesthetics. The observed
    /// case is one recurring cron prompt indexed once per firing, where consecutive rows differ only
    /// by an embedded date: on a ~35-token prompt that is two differing tokens, i.e. an overlap
    /// around 0.94. Genuinely distinct prose - even two rows about the same subsystem - lands far
    /// below 0.5 in practice, because natural language reuses function words but not content words.
    /// 0.85 therefore sits in a wide empty band between the two populations rather than near either.
    /// <para>
    /// The asymmetry of the two failure modes is what fixes the direction of the error budget. Under-
    /// collapsing merely leaves the pre-existing crowding in place; over-collapsing silently deletes
    /// information the caller asked for and can never see was withheld. A threshold this high is
    /// deliberately biased towards the recoverable failure.
    /// </para>
    /// </remarks>
    private const double NearDuplicateSimilarityThreshold = 0.85d;

    /// <summary>
    /// Minimum token count before token-overlap is trusted as a duplicate signal.
    /// </summary>
    /// <remarks>
    /// Overlap ratios are unstable on very short strings: <c>deploy prod</c> and <c>deploy staging</c>
    /// share half their tokens while being different notes entirely, and a single shared token can
    /// push a two-token pair over any threshold. Below this length only an exact content match may
    /// collapse, which keeps terse notes individually addressable.
    /// </remarks>
    private const int MinimumTokensForOverlapComparison = 8;

    /// <summary>
    /// Ranks candidates and returns the top <paramref name="limit"/> entries, most relevant first.
    /// </summary>
    public static IReadOnlyList<MemoryEntry> Rank(
        IReadOnlyCollection<MemoryRankingCandidate> candidates,
        int limit,
        double lambda)
        => RankWithScores(candidates, limit, lambda).Select(scored => scored.Entry).ToList();

    /// <summary>
    /// Ranks candidates exactly as <see cref="Rank"/> does, but also returns the fused relevance
    /// magnitude that produced each position.
    /// </summary>
    /// <remarks>
    /// This is the ordering function itself, not a parallel scoring pass: <see cref="Rank"/> is
    /// implemented on top of it, so the score a caller renders or thresholds is by construction the
    /// score that decided the order. Recomputing relevance anywhere downstream would let the two
    /// definitions drift (#2781).
    /// </remarks>
    public static IReadOnlyList<ScoredMemoryEntry> RankWithScores(
        IReadOnlyCollection<MemoryRankingCandidate> candidates,
        int limit,
        double lambda)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        if (candidates.Count == 0 || limit <= 0)
            return [];

        var anySimilarity = candidates.Any(candidate => candidate.Similarity.HasValue);

        IEnumerable<ScoredMemoryEntry> scored;

        // No usable vector anywhere: reproduce the original lexical ranking exactly.
        if (!anySimilarity)
        {
            scored = candidates
                .Select(candidate => new ScoredMemoryEntry(
                    candidate.Entry,
                    candidate.LexicalScore * Decay(candidate.AgeDays, lambda)));
        }
        else
        {
            var maxLexical = candidates.Max(candidate => candidate.LexicalScore);

            scored = candidates
                .Select(candidate =>
                {
                    var lexicalNormalised = maxLexical > 0d ? candidate.LexicalScore / maxLexical : 0d;
                    var similarityNormalised = candidate.Similarity is { } similarity
                        ? (similarity + 1d) / 2d
                        : NeutralSimilarityPrior;

                    var fused = (LexicalWeight * lexicalNormalised) + (SimilarityWeight * similarityNormalised);
                    return new ScoredMemoryEntry(candidate.Entry, fused * Decay(candidate.AgeDays, lambda));
                });
        }

        // Diversity is applied after scoring and before truncation, so a suppressed near-duplicate
        // frees its slot for the next distinct row instead of shrinking the result set (#2782).
        return CollapseNearDuplicates(scored.OrderByDescending(entry => entry.Score).ToList())
            .Take(limit)
            .ToList();
    }

    /// <summary>
    /// Collapses groups of near-identical rows to one representative, preserving the group's ranking
    /// position and returning the most recent member of each group.
    /// </summary>
    /// <remarks>
    /// Score proximity is deliberately never an input. Two unrelated rows routinely score alike, and
    /// suppressing on that basis would delete real answers - a strictly worse defect than the
    /// crowding this pass exists to fix. Only content overlap can suppress.
    /// <para>
    /// The representative is the most recent member, but it inherits the group's best score. Recency
    /// decides <em>which</em> row is shown - the newest copy of a recurring note is the one whose
    /// details are still true - while the best score decides <em>where</em>, so collapsing a group
    /// can never demote it below rows it previously outranked.
    /// </para>
    /// </remarks>
    private static List<ScoredMemoryEntry> CollapseNearDuplicates(List<ScoredMemoryEntry> ordered)
    {
        if (ordered.Count < 2)
            return ordered;

        var groups = new List<(ScoredMemoryEntry Representative, double BestScore, HashSet<string> Tokens, string Content)>();

        foreach (var entry in ordered)
        {
            var content = entry.Entry.Content ?? string.Empty;
            var tokens = Tokenise(content);
            var absorbed = false;

            for (var index = 0; index < groups.Count; index++)
            {
                var group = groups[index];
                if (!IsNearDuplicate(tokens, content, group.Tokens, group.Content))
                    continue;

                var representative = entry.Entry.CreatedAt > group.Representative.Entry.CreatedAt
                    ? entry
                    : group.Representative;

                groups[index] = (representative, Math.Max(group.BestScore, entry.Score), group.Tokens, group.Content);
                absorbed = true;
                break;
            }

            if (!absorbed)
                groups.Add((entry, entry.Score, tokens, content));
        }

        return groups
            .Select(group => new ScoredMemoryEntry(group.Representative.Entry, group.BestScore))
            .OrderByDescending(entry => entry.Score)
            .ToList();
    }

    private static bool IsNearDuplicate(
        HashSet<string> tokens,
        string content,
        HashSet<string> otherTokens,
        string otherContent)
    {
        // Short content cannot be judged by overlap, so it collapses only on an exact match.
        if (tokens.Count < MinimumTokensForOverlapComparison || otherTokens.Count < MinimumTokensForOverlapComparison)
            return string.Equals(content.Trim(), otherContent.Trim(), StringComparison.OrdinalIgnoreCase);

        var intersection = tokens.Count(token => otherTokens.Contains(token));
        var union = tokens.Count + otherTokens.Count - intersection;

        return union > 0 && (double)intersection / union >= NearDuplicateSimilarityThreshold;
    }

    private static HashSet<string> Tokenise(string content)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        var start = -1;

        for (var index = 0; index <= content.Length; index++)
        {
            var isTokenChar = index < content.Length && char.IsLetterOrDigit(content[index]);

            if (isTokenChar)
            {
                if (start < 0)
                    start = index;
            }
            else if (start >= 0)
            {
                tokens.Add(content[start..index].ToLowerInvariant());
                start = -1;
            }
        }

        return tokens;
    }

    private static double Decay(double ageDays, double lambda)
        => Math.Exp(-lambda * Math.Max(0d, ageDays));
}
