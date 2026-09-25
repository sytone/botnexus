using System.Net;
using System.Text;
using System.Text.Json;
using BotNexus.Gateway.Abstractions.Security;

namespace BotNexus.Gateway.A2A;

/// <summary>Discovers a same-origin A2A endpoint and executes one bounded synchronous JSON-RPC delegation.</summary>
public sealed class A2AClient : IDisposable
{
    private const string AgentCardPath = "/.well-known/agent-card.json";
    private const string TransportFailure = "The A2A transport failed.";
    private const string ProtocolFailure = "The A2A response failed protocol validation.";
    private const string RemoteFailure = "The remote A2A agent reported an error.";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient httpClient;

    /// <summary>Creates a client whose owned transport rejects redirects and pins connections to SSRF-validated addresses.</summary>
    public A2AClient()
    {
        httpClient = new HttpClient(new A2AHttpTransport(), disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    internal A2AClient(HttpClient httpClient)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    internal TimeSpan OwnedTransportTimeout => httpClient.Timeout;

    /// <summary>Releases the owned HTTP pipeline after the client is no longer needed.</summary>
    public void Dispose() => httpClient.Dispose();

    /// <summary>Discovers and negotiates the v1.0 JSON-RPC interface advertised beneath an approved origin.</summary>
    public async Task<A2ADiscoveryResult> DiscoverAsync(
        A2AClientOptions options,
        DateTimeOffset deadline,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateBounds(options);
        var cardUri = new Uri(options.ApprovedOrigin, AgentCardPath);
        EnsureApprovedDestination(cardUri, options);

        using var deadlineCts = CreateDeadlineToken(deadline, cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, cardUri);
        if (options.AuthenticateDiscoveryAsync is not null)
            await options.AuthenticateDiscoveryAsync(request, deadlineCts.Token).ConfigureAwait(false);
        EnsureApprovedDestination(request.RequestUri ?? throw new A2AProtocolException("Authentication removed the discovery destination."), options);

        using var response = await httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, deadlineCts.Token).ConfigureAwait(false);

        RejectRedirect(response);
        if (!response.IsSuccessStatusCode)
            throw new A2AProtocolException("Agent-card discovery returned a non-success HTTP status.");

        await using var content = await ReadBoundedAsync(response, options.MaxAgentCardBytes, deadlineCts.Token).ConfigureAwait(false);
        AgentCardWire? card;
        try
        {
            card = await JsonSerializer.DeserializeAsync<AgentCardWire>(content, JsonOptions, deadlineCts.Token).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new A2AProtocolException("The agent card is malformed JSON.", ex);
        }

        if (card is null || string.IsNullOrWhiteSpace(card.Name))
            throw new A2AProtocolException("The agent card is missing a name.");
        var selected = card.SupportedInterfaces?.FirstOrDefault(candidate =>
            string.Equals(candidate.ProtocolBinding, "JSONRPC", StringComparison.OrdinalIgnoreCase)
            && string.Equals(candidate.ProtocolVersion, "1.0", StringComparison.Ordinal));
        if (selected is null || !Uri.TryCreate(selected.Url, UriKind.Absolute, out var endpoint))
            throw new A2AProtocolException("The agent card has no supported JSON-RPC interface.");

        EnsureApprovedDestination(endpoint, options);
        return new A2ADiscoveryResult(card.Name, "1.0", cardUri, endpoint);
    }

    /// <summary>Sends one message and returns exactly one terminal projection; intermediate status events are not modeled as turns.</summary>
    public async Task<A2ATaskResult> SendMessageAsync(
        A2AClientOptions options,
        A2AMessage message,
        DateTimeOffset deadline,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(message);
        ValidateOutbound(options, message);
        if (deadline <= DateTimeOffset.UtcNow)
            return Failure(A2ATerminalOutcome.DeadlineExpired, "The A2A deadline elapsed before discovery.");

        CancellationTokenSource? activeDeadline = null;
        try
        {
            var discovery = await DiscoverAsync(options, deadline, cancellationToken).ConfigureAwait(false);
            using var deadlineCts = CreateDeadlineToken(deadline, cancellationToken);
            activeDeadline = deadlineCts;
            var requestId = Guid.NewGuid().ToString("N");
            var requestBody = new RpcRequest(
                "2.0",
                requestId,
                "SendMessage",
                new SendMessageParams(
                    new MessageWire(Guid.NewGuid().ToString("N"), "ROLE_USER", message.ContextId, [new TextPartWire(message.Text)]),
                    new SendMessageConfiguration(false)));
            var requestBytes = JsonSerializer.SerializeToUtf8Bytes(requestBody, JsonOptions);
            if (requestBytes.Length > options.MaxRequestBytes)
                throw new ArgumentException("The serialized A2A request exceeds its configured byte limit.", nameof(message));

            using var request = new HttpRequestMessage(HttpMethod.Post, discovery.Endpoint)
            {
                Content = new ByteArrayContent(requestBytes)
            };
            request.Content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
            request.Headers.TryAddWithoutValidation("A2A-Version", discovery.ProtocolVersion);
            EnsureApprovedDestination(discovery.Endpoint, options);
            if (options.AuthenticateSubmissionAsync is not null)
                await options.AuthenticateSubmissionAsync(request, deadlineCts.Token).ConfigureAwait(false);
            EnsureApprovedDestination(request.RequestUri ?? throw new A2AProtocolException("Authentication removed the submission destination."), options);

            using var response = await httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, deadlineCts.Token).ConfigureAwait(false);

            RejectRedirect(response);
            if (!response.IsSuccessStatusCode)
                return Failure(A2ATerminalOutcome.TransportFailed, "The A2A submission returned a non-success HTTP status.");

            await using var content = await ReadBoundedAsync(response, options.MaxResponseBytes, deadlineCts.Token).ConfigureAwait(false);
            RpcResponse? rpc;
            try
            {
                rpc = await JsonSerializer.DeserializeAsync<RpcResponse>(content, JsonOptions, deadlineCts.Token).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                throw new A2AProtocolException("The A2A response is malformed JSON.", ex);
            }

            ValidateRpcEnvelope(rpc, requestId);
            if (rpc?.Error is not null)
                return Failure(A2ATerminalOutcome.Failed, RemoteFailure);

            var task = rpc?.Result?.Task
                ?? throw new A2AProtocolException("The A2A response omitted its task result.");
            if (task.Status is null)
                throw new A2AProtocolException("The A2A response omitted its task status.");

            var provenance = new A2AProvenance(
                discovery.AgentName, discovery.AgentCardUri, discovery.Endpoint, discovery.ProtocolVersion);
            var outcome = MapState(task.Status.State);
            return new A2ATaskResult(
                outcome,
                task.ContextId,
                task.Id,
                ExtractSummary(task.Status.Message, task.Artifacts, options.MaxSummaryCharacters),
                ExtractArtifacts(task.Artifacts, options),
                provenance,
                ExtractUsage(task.Metadata),
                outcome switch
                {
                    A2ATerminalOutcome.Failed => "The remote A2A task failed.",
                    A2ATerminalOutcome.PolicyBlocked => "The remote A2A task was blocked by policy.",
                    _ => null
                });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure(A2ATerminalOutcome.Cancelled, "The A2A call was cancelled.");
        }
        catch (OperationCanceledException) when (
            activeDeadline?.IsCancellationRequested == true || DateTimeOffset.UtcNow >= deadline)
        {
            return Failure(A2ATerminalOutcome.DeadlineExpired, "The A2A deadline elapsed.");
        }
        catch (OperationCanceledException)
        {
            return Failure(A2ATerminalOutcome.TransportFailed, TransportFailure);
        }
        catch (HttpRequestException)
        {
            return Failure(A2ATerminalOutcome.TransportFailed, TransportFailure);
        }
        catch (A2AProtocolException)
        {
            return Failure(A2ATerminalOutcome.TransportFailed, ProtocolFailure);
        }
    }

    private static void ValidateBounds(A2AClientOptions options)
    {
        if (options.MaxAgentCardBytes <= 0 || options.MaxResponseBytes <= 0 || options.MaxRequestBytes <= 0
            || options.MaxMessageTextCharacters <= 0 || options.MaxContextIdCharacters <= 0
            || options.MaxSummaryCharacters <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "All A2A size bounds must be positive.");
        }
    }

    private static void ValidateOutbound(A2AClientOptions options, A2AMessage message)
    {
        ValidateBounds(options);
        if (string.IsNullOrWhiteSpace(message.Text))
            throw new ArgumentException("The A2A objective must not be empty.", nameof(message));
        if (message.Text.Length > options.MaxMessageTextCharacters)
            throw new ArgumentException("The A2A objective exceeds its configured character limit.", nameof(message));
        if (message.ContextId?.Length > options.MaxContextIdCharacters)
            throw new ArgumentException("The A2A context identifier exceeds its configured character limit.", nameof(message));

        // Account for JSON framing before discovery. The final request is measured again after fresh IDs are generated.
        var conservativeBytes = Encoding.UTF8.GetByteCount(message.Text)
            + (message.ContextId is null ? 0 : Encoding.UTF8.GetByteCount(message.ContextId))
            + 256;
        if (conservativeBytes > options.MaxRequestBytes)
            throw new ArgumentException("The A2A request exceeds its configured byte limit.", nameof(message));
    }

    private static void ValidateRpcEnvelope(RpcResponse? rpc, string requestId)
    {
        if (rpc is null
            || !string.Equals(rpc.Jsonrpc, "2.0", StringComparison.Ordinal)
            || !string.Equals(rpc.Id, requestId, StringComparison.Ordinal)
            || (rpc.Result is null) == (rpc.Error is null))
        {
            throw new A2AProtocolException("The JSON-RPC response envelope is invalid.");
        }
    }

    private static void EnsureApprovedDestination(Uri uri, A2AClientOptions options)
    {
        var verdict = SsrfValidator.Validate(uri, options.AdditionalBlockedHosts);
        if (!verdict.IsSafe)
            throw new A2AProtocolException("The A2A destination failed SSRF validation.");
        if (!SameOrigin(options.ApprovedOrigin, uri))
            throw new A2AProtocolException("The A2A destination escaped the operator-approved origin.");
    }

    private static bool SameOrigin(Uri approved, Uri candidate) =>
        string.Equals(approved.Scheme, candidate.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(approved.IdnHost, candidate.IdnHost, StringComparison.OrdinalIgnoreCase)
        && approved.Port == candidate.Port
        && string.IsNullOrEmpty(candidate.UserInfo);

    private static void RejectRedirect(HttpResponseMessage response)
    {
        if ((int)response.StatusCode is >= 300 and < 400)
            throw new A2AProtocolException("A2A redirects are not allowed.");
    }

    private static async Task<MemoryStream> ReadBoundedAsync(
        HttpResponseMessage response, int maximumBytes, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is long length && length > maximumBytes)
            throw new A2AProtocolException("The A2A payload exceeded its configured size limit.");

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var target = new MemoryStream(Math.Min(maximumBytes, 81920));
        var buffer = new byte[81920];
        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            if (target.Length + read > maximumBytes)
            {
                await target.DisposeAsync().ConfigureAwait(false);
                throw new A2AProtocolException("The A2A payload exceeded its configured size limit.");
            }
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        target.Position = 0;
        return target;
    }

    private static CancellationTokenSource CreateDeadlineToken(
        DateTimeOffset deadline, CancellationToken cancellationToken)
    {
        var remaining = deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
            throw new OperationCanceledException("The A2A deadline elapsed.");

        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(remaining);
        return source;
    }

    private static A2ATerminalOutcome MapState(string? state) => state switch
    {
        "TASK_STATE_COMPLETED" => A2ATerminalOutcome.Completed,
        "TASK_STATE_INPUT_REQUIRED" or "TASK_STATE_AUTH_REQUIRED" => A2ATerminalOutcome.InputRequired,
        "TASK_STATE_FAILED" => A2ATerminalOutcome.Failed,
        "TASK_STATE_CANCELED" => A2ATerminalOutcome.Cancelled,
        "TASK_STATE_REJECTED" => A2ATerminalOutcome.PolicyBlocked,
        _ => throw new A2AProtocolException("The synchronous A2A response has an unsupported state.")
    };

    private static string? ExtractSummary(
        MessageResultWire? statusMessage,
        IReadOnlyList<ArtifactWire>? artifacts,
        int maximumCharacters)
    {
        var text = statusMessage?.Parts?.FirstOrDefault(part => !string.IsNullOrEmpty(part.Text))?.Text
            ?? artifacts?.SelectMany(artifact => artifact.Parts ?? [])
                .FirstOrDefault(part => !string.IsNullOrEmpty(part.Text))?.Text;
        return text is null || text.Length <= maximumCharacters
            ? text
            : string.Concat(text.AsSpan(0, maximumCharacters), "\u2026");
    }

    private static IReadOnlyList<A2AArtifactReference> ExtractArtifacts(
        IReadOnlyList<ArtifactWire>? artifacts,
        A2AClientOptions options)
    {
        if (artifacts is null)
            return [];

        var references = new List<A2AArtifactReference>();
        foreach (var artifact in artifacts)
        {
            foreach (var part in artifact.Parts ?? [])
            {
                if (part.Url is null)
                    continue;
                if (!Uri.TryCreate(part.Url, UriKind.Absolute, out var uri))
                    throw new A2AProtocolException("An artifact URL is malformed.");
                EnsureApprovedDestination(uri, options);
                references.Add(new A2AArtifactReference(artifact.ArtifactId, artifact.Name, uri, part.MediaType));
            }
        }
        return references;
    }

    private static A2AUsage? ExtractUsage(JsonElement? metadata)
    {
        if (metadata is not { ValueKind: JsonValueKind.Object } value
            || !value.TryGetProperty("usage", out var usage)
            || usage.ValueKind != JsonValueKind.Object)
            return null;

        long? Read(string name) => usage.TryGetProperty(name, out var token) && token.TryGetInt64(out var count) ? count : null;
        return new A2AUsage(Read("inputTokens"), Read("outputTokens"));
    }

    private static A2ATaskResult Failure(A2ATerminalOutcome outcome, string error) =>
        new(outcome, null, null, null, [], null, null, error);

    private sealed record AgentCardWire(string? Name, IReadOnlyList<InterfaceWire>? SupportedInterfaces);
    private sealed record InterfaceWire(string? Url, string? ProtocolBinding, string? ProtocolVersion);
    private sealed record RpcRequest(string Jsonrpc, string Id, string Method, SendMessageParams Params);
    private sealed record SendMessageParams(MessageWire Message, SendMessageConfiguration Configuration);
    private sealed record SendMessageConfiguration(bool ReturnImmediately);
    private sealed record MessageWire(string MessageId, string Role, string? ContextId, IReadOnlyList<TextPartWire> Parts);
    private sealed record TextPartWire(string Text);
    private sealed record RpcResponse(string? Jsonrpc, string? Id, SendMessageResultWire? Result, RpcErrorWire? Error);
    private sealed record SendMessageResultWire(TaskWire? Task, MessageResultWire? Message);
    private sealed record RpcErrorWire(int Code, string Message);
    private sealed record TaskWire(string? Id, string? ContextId, TaskStatusWire? Status, IReadOnlyList<ArtifactWire>? Artifacts, JsonElement? Metadata);
    private sealed record TaskStatusWire(string? State, MessageResultWire? Message);
    private sealed record MessageResultWire(IReadOnlyList<ResultPartWire>? Parts);
    private sealed record ResultPartWire(string? Text);
    private sealed record ArtifactWire(string? ArtifactId, string? Name, IReadOnlyList<ArtifactPartWire>? Parts);
    private sealed record ArtifactPartWire(string? Url, string? MediaType, string? Text);
}
