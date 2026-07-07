// BotNexus webhook sender example — all response modes.
//
// Demonstrates how to talk to the BotNexus webhook API from C#:
//
//   1. Register an inbound webhook via POST api/webhooks/registrations and
//      capture the one-time secret.
//   2. Sign each inbound delivery with HMAC-SHA256 using HMACSHA256, matching
//      the X-BotNexus-Signature-256 header the gateway verifies (same
//      convention as GitHub/Stripe).
//   3. Send an inbound message in every response mode:
//        * async    — 202 + poll GET api/webhooks/runs/{runId} for the result.
//        * sync     — 200 with the agent response inline.
//        * callback — 202; the gateway POSTs the result to your callbackUrl.
//
// Run it directly against a running gateway:
//
//     export BOTNEXUS_WEBHOOK_SECRET=whsec_...      # only if reusing a secret
//     dotnet run
//
// No external NuGet packages — only in-framework HttpClient, HMACSHA256, and
// System.Text.Json. Requires .NET 10+.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BotNexus.Examples.Webhooks;

/// <summary>Response modes accepted by the gateway.</summary>
internal enum ResponseMode
{
    Async,
    Sync,
    Callback,
}

/// <summary>
/// Self-contained console app that exercises the BotNexus webhook API across
/// all three response modes using a single <see cref="HttpClient"/>.
/// </summary>
internal static class WebhookSender
{
    // -----------------------------------------------------------------------
    // Configuration (override via environment variables).
    // -----------------------------------------------------------------------
    private static readonly string BaseUrl =
        (Environment.GetEnvironmentVariable("BOTNEXUS_BASE_URL") ?? "http://localhost:5000")
            .TrimEnd('/');

    private static readonly string AgentId =
        Environment.GetEnvironmentVariable("BOTNEXUS_AGENT_ID") ?? "farnsworth";

    private static readonly string? ApiToken =
        Environment.GetEnvironmentVariable("BOTNEXUS_API_TOKEN");

    // The inbound signing secret. Read from the environment when reusing a
    // secret from a previous registration; otherwise a fresh registration
    // supplies one.
    private const string SecretEnvVar = "BOTNEXUS_WEBHOOK_SECRET";
    private const string WebhookIdEnvVar = "BOTNEXUS_WEBHOOK_ID";

    // Signature header the gateway expects (HMAC-SHA256 of the raw request body).
    private const string SignatureHeader = "X-BotNexus-Signature-256";

    // Compact, stable JSON: the exact bytes we sign are the exact bytes we send.
    // A mismatch (e.g. re-serializing with different spacing after signing)
    // produces a 401 Invalid signature from the gateway.
    private static readonly JsonSerializerOptions SendJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    // The gateway serializes responses as camelCase with string enum names.
    private static readonly JsonSerializerOptions ReadJson = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions PrettyJson = new()
    {
        WriteIndented = true,
    };

    private static readonly HttpClient Http = new()
    {
        BaseAddress = new Uri(BaseUrl),
        Timeout = TimeSpan.FromSeconds(180),
    };

    // -----------------------------------------------------------------------
    // Entry point — exercises both a fresh registration and all three modes.
    // -----------------------------------------------------------------------
    public static async Task<int> Main()
    {
        try
        {
            var (webhookId, secret) = await EnsureRegistrationAsync("csharp-example");

            Console.WriteLine("\n[async] responseMode=async (poll to completion)");
            var asyncResult = await SendInboundAsync(
                webhookId, secret,
                message: "Hello from the C# example.",
                mode: ResponseMode.Async);
            Console.WriteLine(JsonSerializer.Serialize(asyncResult, PrettyJson));

            Console.WriteLine("\n[callback] responseMode=callback (delivered out-of-band)");
            var callbackResult = await SendInboundAsync(
                webhookId, secret,
                message: "Hello via callback mode.",
                mode: ResponseMode.Callback,
                callbackUrl: "https://example.invalid/botnexus/callback");
            Console.WriteLine(JsonSerializer.Serialize(callbackResult, PrettyJson));

            Console.WriteLine("\n[sync] responseMode=sync (inline 200)");
            var syncResult = await SendInboundAsync(
                webhookId, secret,
                message: "Hello from the sync variant.",
                mode: ResponseMode.Sync);
            Console.WriteLine(JsonSerializer.Serialize(syncResult, PrettyJson));

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Demo failed: {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine(
                "Ensure the gateway is running and BOTNEXUS_BASE_URL / " +
                "BOTNEXUS_AGENT_ID / BOTNEXUS_API_TOKEN are set correctly.");
            return 1;
        }
    }

    // -----------------------------------------------------------------------
    // HMAC signing — in-framework only.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Returns the <c>X-BotNexus-Signature-256</c> value for <paramref name="body"/>.
    /// The gateway computes <c>HMAC-SHA256(secret_utf8, raw_body_bytes)</c> and
    /// compares it (constant-time) against the header, formatted as
    /// <c>sha256=&lt;lowercase hex&gt;</c>. Sign the exact bytes you send on the
    /// wire — serialize the JSON body once and reuse those bytes for both
    /// signing and sending.
    /// </summary>
    private static string ComputeSignature(string secret, byte[] body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(body);
        return "sha256=" + Convert.ToHexStringLower(hash);
    }

    /// <summary>Optional bearer-token header for management endpoints.</summary>
    private static void AddAuth(HttpRequestMessage request)
    {
        if (!string.IsNullOrEmpty(ApiToken))
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {ApiToken}");
    }

    // -----------------------------------------------------------------------
    // Registration.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Reuses <c>BOTNEXUS_WEBHOOK_SECRET</c> + <c>BOTNEXUS_WEBHOOK_ID</c> from the
    /// environment when both are present, otherwise creates a fresh registration.
    /// </summary>
    private static async Task<(string WebhookId, string Secret)> EnsureRegistrationAsync(string label)
    {
        var envSecret = Environment.GetEnvironmentVariable(SecretEnvVar);
        var envId = Environment.GetEnvironmentVariable(WebhookIdEnvVar);
        if (!string.IsNullOrEmpty(envSecret) && !string.IsNullOrEmpty(envId))
            return (envId, envSecret);

        var registration = await RegisterWebhookAsync(label);
        var webhookId = registration.GetProperty("webhookId").GetString()
            ?? throw new InvalidOperationException("Registration response missing webhookId.");
        var secret = registration.GetProperty("secret").GetString()
            ?? throw new InvalidOperationException(
                "Registration response missing secret — it is only returned once on create.");
        var url = registration.TryGetProperty("url", out var u) ? u.GetString() : null;
        Console.WriteLine($"Registered webhook {webhookId}; url={url}");
        return (webhookId, secret);
    }

    /// <summary>
    /// Creates a webhook registration and returns the response (which includes
    /// the one-time <c>secret</c> — store it securely; it is never returned again).
    /// </summary>
    private static async Task<JsonElement> RegisterWebhookAsync(
        string label,
        ResponseMode defaultResponseMode = ResponseMode.Async,
        string? conversationId = null)
    {
        var payload = new
        {
            agentId = AgentId,
            label,
            conversationId,
            defaultResponseMode = ModeToWire(defaultResponseMode),
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/registrations")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, SendJson), Encoding.UTF8, "application/json"),
        };
        AddAuth(request);

        using var response = await Http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var stream = await response.Content.ReadAsStreamAsync();
        return (await JsonDocument.ParseAsync(stream)).RootElement.Clone();
    }

    // -----------------------------------------------------------------------
    // Inbound delivery.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Sends a signed inbound webhook delivery and returns the result. For
    /// <see cref="ResponseMode.Sync"/> the returned element is the inline 200
    /// response. For <see cref="ResponseMode.Async"/> this polls the run to
    /// completion and returns the final run object. For
    /// <see cref="ResponseMode.Callback"/> it returns the 202 acceptance.
    /// </summary>
    private static async Task<JsonElement> SendInboundAsync(
        string webhookId,
        string secret,
        string message,
        ResponseMode? mode = null,
        bool? agentAction = null,
        string? callbackUrl = null,
        double pollTimeoutSeconds = 120,
        double pollIntervalSeconds = 2)
    {
        var payload = new
        {
            message,
            responseMode = mode.HasValue ? ModeToWire(mode.Value) : null,
            agentAction,
            callbackUrl,
        };

        // Serialize ONCE — these exact bytes are both signed and sent.
        var body = JsonSerializer.SerializeToUtf8Bytes(payload, SendJson);

        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"/api/webhooks/{AgentId}/{webhookId}")
        {
            // ByteArrayContent sends the exact signed bytes unchanged.
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        request.Headers.TryAddWithoutValidation(SignatureHeader, ComputeSignature(secret, body));

        using var response = await Http.SendAsync(request);
        response.EnsureSuccessStatusCode();

        // Sync mode returns the full agent response inline with 200.
        if ((int)response.StatusCode == 200)
            return await ReadJsonElementAsync(response);

        var accepted = await ReadJsonElementAsync(response);

        // Callback mode: the gateway POSTs the result out-of-band; nothing to poll.
        if (mode == ResponseMode.Callback)
            return accepted;

        // Async mode: poll the run to completion.
        var runId = accepted.GetProperty("runId").GetString()
            ?? throw new InvalidOperationException("Accepted response missing runId.");
        return await PollRunAsync(runId, pollTimeoutSeconds, pollIntervalSeconds);
    }

    /// <summary>Polls <c>GET api/webhooks/runs/{runId}</c> until the run reaches a terminal status.</summary>
    private static async Task<JsonElement> PollRunAsync(
        string runId, double timeoutSeconds, double intervalSeconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/webhooks/runs/{runId}");
            AddAuth(request);
            using var response = await Http.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var run = await ReadJsonElementAsync(response);

            var status = run.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";
            if (status.Equals("completed", StringComparison.OrdinalIgnoreCase) ||
                status.Equals("succeeded", StringComparison.OrdinalIgnoreCase) ||
                status.Equals("failed", StringComparison.OrdinalIgnoreCase) ||
                status.Equals("faulted", StringComparison.OrdinalIgnoreCase) ||
                status.Equals("cancelled", StringComparison.OrdinalIgnoreCase))
            {
                return run;
            }

            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }

        throw new TimeoutException($"Run {runId} did not complete within {timeoutSeconds}s.");
    }

    private static async Task<JsonElement> ReadJsonElementAsync(HttpResponseMessage response)
    {
        var stream = await response.Content.ReadAsStreamAsync();
        return (await JsonDocument.ParseAsync(stream)).RootElement.Clone();
    }

    /// <summary>
    /// Maps the enum to the wire value the gateway expects. The inbound endpoint
    /// deserializes <c>responseMode</c> case-insensitively onto its enum, so the
    /// lowercase names ("async"/"sync"/"callback") bind correctly.
    /// </summary>
    private static string ModeToWire(ResponseMode mode) => mode switch
    {
        ResponseMode.Async => "async",
        ResponseMode.Sync => "sync",
        ResponseMode.Callback => "callback",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };
}
