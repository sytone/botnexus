using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;

namespace BotNexus.E2E.PortalDesktop.Tests;

/// <summary>
/// Optional "lightweight real LLM" channel turn (issue #1962 acceptance criterion).
///
/// <para>Channel e2e coverage is most valuable when at least one path exercises a
/// genuine model turn rather than a canned mock. That is expensive and requires
/// credentials, so it is gated behind the <c>E2E_LLM=1</c> environment flag:</para>
/// <list type="bullet">
///   <item><description>When <c>E2E_LLM</c> is not <c>1</c> the test SKIPS - CI without
///   creds skips cleanly and never silently passes.</description></item>
///   <item><description>When <c>E2E_LLM=1</c> the test expects an OpenAI-compatible
///   endpoint in <c>E2E_LLM_ENDPOINT</c> and a key in <c>E2E_LLM_API_KEY</c>
///   (e.g. GitHub Models: https://models.inference.ai.azure.com). Missing config with
///   the flag ON is a real failure - we do NOT invent credentials.</description></item>
/// </list>
///
/// <para><b>TODO (#1962 follow-up):</b> once the portal e2e harness can stand up a live
/// gateway + desktop portal in CI, drive this turn THROUGH the portal channel (type a
/// prompt, assert a streamed assistant reply) instead of calling the model endpoint
/// directly. This direct call is the minimal, honest scaffold proving the gate works.</para>
/// </summary>
public sealed class LightweightRealLlmTurnTests
{
    private const string LlmFlag = "E2E_LLM";

    [SkippableFact]
    public async Task RealModel_CompletesASingleTurn_WhenEnabled()
    {
        var enabled = string.Equals(
            Environment.GetEnvironmentVariable(LlmFlag),
            "1",
            StringComparison.Ordinal);
        Skip.IfNot(
            enabled,
            $"{LlmFlag} != 1; skipping the live lightweight-LLM turn. " +
            $"Set {LlmFlag}=1 with E2E_LLM_ENDPOINT + E2E_LLM_API_KEY to run a real turn.");

        var endpoint = Environment.GetEnvironmentVariable("E2E_LLM_ENDPOINT");
        var apiKey = Environment.GetEnvironmentVariable("E2E_LLM_API_KEY");
        var model = Environment.GetEnvironmentVariable("E2E_LLM_MODEL") ?? "gpt-4o-mini";

        // With the flag explicitly ON, missing config is a real failure - not a skip.
        // This is the "never silently pass" guarantee: opting in means you must supply creds.
        endpoint.ShouldNotBeNullOrWhiteSpace(
            "E2E_LLM=1 but E2E_LLM_ENDPOINT is unset. Provide an OpenAI-compatible endpoint.");
        apiKey.ShouldNotBeNullOrWhiteSpace(
            "E2E_LLM=1 but E2E_LLM_API_KEY is unset. Provide a real key (none is invented).");

        using var http = new HttpClient { BaseAddress = new Uri(endpoint!) };
        http.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

        var payload = new
        {
            model,
            messages = new[]
            {
                new { role = "system", content = "You are a terse test probe." },
                new { role = "user", content = "Reply with the single word: pong" },
            },
            max_tokens = 16,
            temperature = 0.0,
        };

        using var resp = await http.PostAsJsonAsync("chat/completions", payload);
        resp.EnsureSuccessStatusCode();

        var doc = await resp.Content.ReadFromJsonAsync<System.Text.Json.JsonDocument>();
        var content = doc!
            .RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        content.ShouldNotBeNullOrWhiteSpace("real model returned an empty completion");
    }
}
