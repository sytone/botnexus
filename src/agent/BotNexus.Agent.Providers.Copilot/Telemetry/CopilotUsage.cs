using System.Text.Json;

namespace BotNexus.Agent.Providers.Copilot.Telemetry;

/// <summary>
/// Strongly typed representation of the <c>copilot_usage</c> object that
/// GitHub Copilot tacks onto every successful API response (Anthropic
/// Messages, OpenAI Responses, and OpenAI Chat Completions).
/// </summary>
/// <remarks>
/// Wire shape captured from real Copilot CLI traffic:
/// <code>
/// "copilot_usage": {
///   "token_details": [
///     { "batch_size": 1000000, "cost_per_batch": 30000000000, "token_count": 157, "token_type": "input"      },
///     { "batch_size": 1000000, "cost_per_batch": 15000000000, "token_count":   0, "token_type": "cache_read" },
///     { "batch_size": 1000000, "cost_per_batch":           0, "token_count":   0, "token_type": "cache_write"},
///     { "batch_size": 1000000, "cost_per_batch":120000000000, "token_count":  14, "token_type": "output"     }
///   ],
///   "total_nano_aiu": 6390000
/// }
/// </code>
/// <c>total_nano_aiu</c> is the call's billed premium-interaction cost in
/// nano-AIU. Each <see cref="CopilotTokenDetail"/> entry records the per-token-type
/// breakdown the server used to derive that total, so callers can compute
/// "what would this cost have been if I'd cached more aggressively".
/// </remarks>
public sealed record CopilotUsage(
    long TotalNanoAiu,
    IReadOnlyList<CopilotTokenDetail> TokenDetails);

/// <summary>
/// One entry in <see cref="CopilotUsage.TokenDetails"/>: the per-token-type
/// price the Copilot billing pipeline applied for this call.
/// </summary>
/// <param name="TokenType">
/// One of <c>input</c>, <c>output</c>, <c>cache_read</c>, <c>cache_write</c>
/// at time of writing. Captured verbatim so future Copilot additions are
/// surfaced without code change.
/// </param>
/// <param name="TokenCount">Number of tokens of this type billed.</param>
/// <param name="BatchSize">
/// Batch size used by the pricing engine (constant 1_000_000 in current captures).
/// </param>
/// <param name="CostPerBatch">
/// Cost in nano-AIU per <see cref="BatchSize"/> tokens. The contribution to
/// <see cref="CopilotUsage.TotalNanoAiu"/> is approximately
/// <c>TokenCount * CostPerBatch / BatchSize</c>.
/// </param>
public sealed record CopilotTokenDetail(
    string TokenType,
    long TokenCount,
    long BatchSize,
    long CostPerBatch);
