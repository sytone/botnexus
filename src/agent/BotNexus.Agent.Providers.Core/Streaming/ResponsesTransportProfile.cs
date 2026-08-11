using System.Net.Http;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Gateway.Abstractions.Security;

namespace BotNexus.Agent.Providers.Core.Streaming;

/// <summary>
/// The build-payload delegate for the Responses transport. The provider shell supplies its own
/// (project-internal) <c>*RequestBuilder.Build</c> bound to the shared
/// <see cref="ResponsesMessageConverter"/>; the engine in Providers.Core invokes it without
/// referencing the per-provider builder type.
/// </summary>
public delegate System.Text.Json.Nodes.JsonObject ResponsesPayloadBuilder(
    LlmModel model,
    string? systemPrompt,
    IReadOnlyList<Message> messages,
    IReadOnlyList<Tool>? tools,
    StreamOptions? options);

/// <summary>
/// The parse delegate for the Responses transport. The provider shell supplies its own
/// (project-internal) <c>*ResponsesStreamParser.ParseAsync</c>; the engine invokes it to drain the
/// SSE stream into the <see cref="LlmStream"/>.
/// </summary>
public delegate Task ResponsesStreamParse(
    LlmStream stream,
    StreamReader reader,
    LlmModel model,
    StreamOptions? options,
    string api,
    Action<LlmStream, LlmModel, string, IReadOnlyList<ContentBlock>?> emitError,
    CancellationToken ct);

/// <summary>
/// Captures the per-provider deltas the shared <see cref="ResponsesStreamEngine"/> needs to drive a
/// specific Responses transport (OpenAI vs Copilot). Everything not in this profile — the request
/// loop, the message/tool conversion, and the error/abort emit shapes — is identical across the two
/// providers and lives in Providers.Core.
/// </summary>
/// <param name="Api">The provider <c>Api</c> identifier (e.g. <c>openai-responses</c>).</param>
/// <param name="ActivityName">The diagnostic activity span name for the stream.</param>
/// <param name="BuildPayload">Builds the request body via the provider's own request builder.</param>
/// <param name="Parse">Drains the SSE response via the provider's own stream parser.</param>
/// <param name="DecorateHeaders">
/// Applies the provider's dynamic headers to the outbound request, given the request, model, the
/// original (untransformed) context messages, and the stream options. OpenAI applies Copilot headers
/// only when the model routes through github-copilot; the Copilot transport always applies them (plus
/// its resolved interaction id).
/// </param>
/// <param name="ThrowForError">
/// Projects a non-success HTTP response into the provider's exception shape — a plain
/// <see cref="System.Net.Http.HttpRequestException"/> for OpenAI, or the richer
/// <c>ProviderHttpErrorHelper</c> projection for Copilot.
/// </param>
/// <param name="OnResponseHeaders">
/// Optional hook invoked with the raw response immediately after send. Copilot uses it to emit
/// response-header telemetry to the current activity; OpenAI leaves it null.
/// </param>
/// <param name="SecretRedactor">
/// Optional secret redactor for the untrusted non-2xx error body (#2881). Supplied by the provider
/// shell and handed to <paramref name="ThrowForError"/> by the engine so the body is scrubbed at the
/// single choke point before it lands in a persisted, user-visible exception message. Null is a
/// deliberate no-op so an unwired provider keeps its diagnostics rather than losing them.
/// </param>
public sealed record ResponsesTransportProfile(
    string Api,
    string ActivityName,
    ResponsesPayloadBuilder BuildPayload,
    ResponsesStreamParse Parse,
    Action<HttpRequestMessage, LlmModel, IReadOnlyList<Message>, StreamOptions?> DecorateHeaders,
    Action<HttpResponseMessage, string, ISecretRedactor?> ThrowForError,
    Action<HttpResponseMessage>? OnResponseHeaders = null,
    ISecretRedactor? SecretRedactor = null);
