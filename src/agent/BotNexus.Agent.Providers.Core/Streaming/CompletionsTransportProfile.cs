using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Agent.Providers.Core.Compatibility;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Gateway.Abstractions.Security;

namespace BotNexus.Agent.Providers.Core.Streaming;

/// <summary>
/// Captures the small set of per-provider deltas the shared <see cref="CompletionsStreamEngine"/>
/// needs to drive a specific Chat Completions transport (OpenAI vs Copilot). Everything not in this
/// profile is identical across the two providers and lives in the engine.
/// </summary>
/// <param name="Api">The provider <c>Api</c> identifier (e.g. <c>openai-completions</c>).</param>
/// <param name="ActivityName">The diagnostic activity span name for the stream.</param>
/// <param name="BuildPayload">
/// Builds the request body from the provider's own <c>*RequestBuilder</c> (which binds the provider's
/// message converter and the engine's <see cref="CompletionsStreamEngine.ConvertTools"/>). Kept as a
/// delegate so the engine in Providers.Core never references a per-provider builder type.
/// </param>
/// <param name="DecorateHeaders">
/// Applies the provider's dynamic headers to the outbound request, given the request, model, the
/// transformed message list, and the stream options. OpenAI applies Copilot headers only when the
/// model routes through github-copilot; the Copilot transport always applies them (plus its resolved
/// interaction id).
/// </param>
/// <param name="ThrowForError">
/// Projects a non-success HTTP response into the provider's exception shape - a plain
/// <see cref="System.Net.Http.HttpRequestException"/> for OpenAI, or the richer
/// <c>ProviderHttpErrorHelper</c> projection for Copilot. The third argument is the optional secret
/// redactor the engine hands down from <see cref="SecretRedactor"/>; the delegate must forward it to
/// <c>ProviderHttpErrorHelper.ThrowForFailedResponse</c> so the untrusted error body is scrubbed at
/// the single choke point before it is interpolated into a persisted, user-visible message (#2881).
/// </param>
/// <param name="OnResponseHeaders">
/// Optional hook invoked with the raw response immediately after send. Copilot uses it to emit
/// response-header telemetry to the current activity; OpenAI leaves it null.
/// </param>
/// <param name="InspectChunk">
/// Optional per-SSE-chunk inspection hook. Copilot uses it to emit usage telemetry; OpenAI leaves it
/// null.
/// </param>
/// <param name="SecretRedactor">
/// Optional secret redactor for the untrusted non-2xx error body (#2881). Supplied by the provider
/// shell from its own injected redactor and handed to <paramref name="ThrowForError"/> by the engine.
/// Null is a deliberate no-op so an unwired provider keeps its diagnostics rather than losing them.
/// </param>
public sealed record CompletionsTransportProfile(
    string Api,
    string ActivityName,
    Func<LlmModel, string?, IReadOnlyList<Message>, IReadOnlyList<Tool>?, StreamOptions?, OpenAICompletionsCompat, JsonObject> BuildPayload,
    Action<HttpRequestMessage, LlmModel, IReadOnlyList<Message>, StreamOptions?> DecorateHeaders,
    Action<HttpResponseMessage, string, ISecretRedactor?> ThrowForError,
    Action<HttpResponseMessage>? OnResponseHeaders = null,
    Action<JsonElement>? InspectChunk = null,
    ISecretRedactor? SecretRedactor = null);
