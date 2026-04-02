using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using BotNexus.Tests.Deployment.Infrastructure;

namespace BotNexus.Tests.Deployment.Tests;

/// <summary>
/// SC-GSG-001: Getting-Started Guide — validates every step in docs/getting-started.md
/// by simulating a real user's first experience from clean install to talking to an agent.
/// Uses real process starts (not WebApplicationFactory) with isolated temp BOTNEXUS_HOME.
/// </summary>
[Trait("Category", "Deployment")]
[Trait("Category", "GettingStarted")]
public sealed class GettingStartedGuideTests
{
    /// <summary>
    /// Walks through the getting-started guide end-to-end:
    ///   §2 Build → §3 First Run → §3 Health/Ready → §4 Provider → §5 Agent → §6 Message → Extensions → Agents API
    /// </summary>
    [Fact]
    public async Task GettingStartedGuide_FullJourney_SucceedsOnCleanInstall()
    {
        // ── §2: Build from Source ─────────────────────────────────────────────
        // The guide says: "dotnet build BotNexus.slnx"
        // We verify the Gateway DLL exists (the test harness requires a prior build).
        Assert.True(File.Exists(GatewayProcessFixture.GatewayDllPath),
            $"Gateway DLL must exist at {GatewayProcessFixture.GatewayDllPath}. " +
            "Run 'dotnet build BotNexus.slnx' first (matches getting-started §2).");

        var port = GatewayProcessFixture.FindFreePort();
        await using var gw = new GatewayProcessFixture(port);

        // ── §3: First Run — start Gateway with empty BOTNEXUS_HOME ───────────
        // The guide says the Gateway auto-creates ~/.botnexus/ with default structure.
        // We write appsettings.json to set the port but do NOT write config.json
        // so BotNexusHome.Initialize() creates the default directory structure.
        gw.WriteAppSettings(AppSettingsWithPort(port));

        await gw.StartAsync();
        await gw.WaitForHealthyAsync(TimeSpan.FromSeconds(30));

        // §3: Verify home directory structure matches the guide's diagram
        Directory.Exists(gw.BotNexusHome).Should().BeTrue("~/.botnexus/ should be created on first run");
        File.Exists(gw.ConfigJsonPath).Should().BeTrue("config.json should be auto-created (§3)");
        Directory.Exists(Path.Combine(gw.BotNexusHome, "extensions")).Should().BeTrue("extensions/ (§3)");
        Directory.Exists(Path.Combine(gw.BotNexusHome, "extensions", "providers")).Should().BeTrue("extensions/providers/ (§3)");
        Directory.Exists(Path.Combine(gw.BotNexusHome, "extensions", "channels")).Should().BeTrue("extensions/channels/ (§3)");
        Directory.Exists(Path.Combine(gw.BotNexusHome, "extensions", "tools")).Should().BeTrue("extensions/tools/ (§3)");
        Directory.Exists(Path.Combine(gw.BotNexusHome, "agents")).Should().BeTrue("agents/ (§3)");
        Directory.Exists(Path.Combine(gw.BotNexusHome, "tokens")).Should().BeTrue("tokens/ (§3)");
        Directory.Exists(Path.Combine(gw.BotNexusHome, "sessions")).Should().BeTrue("sessions/ (§3)");
        Directory.Exists(Path.Combine(gw.BotNexusHome, "logs")).Should().BeTrue("logs/ (§3)");

        // ── §3: Health check — GET /health ────────────────────────────────────
        // The guide says: curl http://localhost:18790/health → {"status": "Healthy", ...}
        using var client = gw.CreateHttpClient();
        var healthResponse = await client.GetAsync("/health");
        healthResponse.StatusCode.Should().Be(HttpStatusCode.OK, "/health must return 200 (§3)");

        var healthJson = await healthResponse.Content.ReadFromJsonAsync<JsonElement>();
        var healthStatus = healthJson.GetProperty("status").GetString();
        healthStatus.Should().NotBe("Unhealthy",
            "Getting-started first run must NOT be Unhealthy — this is the bug Jon hit");
        healthStatus.Should().BeOneOf("Healthy", "Degraded",
            "/health status should be Healthy or Degraded on clean install (§3)");

        // ── §3: Ready check — GET /ready ──────────────────────────────────────
        var readyResponse = await client.GetAsync("/ready");
        readyResponse.StatusCode.Should().Be(HttpStatusCode.OK, "/ready must return 200 (§3)");

        var readyJson = await readyResponse.Content.ReadFromJsonAsync<JsonElement>();
        readyJson.GetProperty("status").GetString().Should().NotBeNullOrWhiteSpace(
            "/ready must have a status field (§3)");

        // ── Stop for reconfiguration (§4 + §5 happen with a restart) ──────────
        await gw.StopAsync();

        // ── §4: Configure Provider ────────────────────────────────────────────
        // The guide adds copilot provider in config.json. We use the fixture mock
        // provider instead (avoids real OAuth) but follow the same config shape.
        // Deploy the fixture provider extension DLL.
        if (!File.Exists(GatewayProcessFixture.ExtensionE2EDllPath))
        {
            Assert.Fail(
                $"Extension E2E DLL not found at {GatewayProcessFixture.ExtensionE2EDllPath}. " +
                "Build the full solution first.");
            return;
        }

        gw.DeployExtension("providers", "fixture-provider", GatewayProcessFixture.ExtensionE2EDllPath);

        // ── §5: Create Your First Agent ───────────────────────────────────────
        // The guide shows editing config.json with a named agent.
        // We write the config matching the guide's structure (provider + agent).
        gw.WriteConfigJson("""
            {
              "BotNexus": {
                "ExtensionsPath": "~/.botnexus/extensions",
                "Providers": {
                  "fixture-provider": {
                    "DefaultModel": "fixture-model"
                  }
                },
                "Agents": {
                  "Model": "fixture-model",
                  "MaxTokens": 8192,
                  "Temperature": 0.1,
                  "Workspace": "~/.botnexus",
                  "Named": {
                    "assistant": {
                      "Name": "assistant",
                      "Provider": "fixture-provider",
                      "Model": "fixture-model",
                      "EnableMemory": true
                    }
                  }
                }
              }
            }
            """);

        // ── Restart (guide says: Ctrl+C then dotnet run again) ────────────────
        await gw.StartAsync();
        await gw.WaitForHealthyAsync(TimeSpan.FromSeconds(30));

        // §4: Verify provider loaded via /api/extensions
        using var client2 = gw.CreateHttpClient();
        var extResponse = await client2.GetAsync("/api/extensions");
        extResponse.StatusCode.Should().Be(HttpStatusCode.OK, "GET /api/extensions must return 200 (§4)");

        var extJson = await extResponse.Content.ReadFromJsonAsync<JsonElement>();
        extJson.GetProperty("loaded").GetInt32().Should().BeGreaterThan(0,
            "at least one extension should be loaded after adding the provider (§4)");
        extJson.GetProperty("providers").GetInt32().Should().BeGreaterThanOrEqualTo(1,
            "provider count should be ≥1 after configuring fixture-provider (§4)");

        var extResults = extJson.GetProperty("results").EnumerateArray().ToList();
        extResults.Should().Contain(r =>
            r.GetProperty("key").GetString() == "fixture-provider" &&
            r.GetProperty("success").GetBoolean(),
            "fixture-provider extension should load successfully (§4)");

        // §5: Verify agent listed via /api/agents
        var agentsResponse = await client2.GetAsync("/api/agents");
        agentsResponse.StatusCode.Should().Be(HttpStatusCode.OK, "GET /api/agents must return 200 (§5)");

        var agentsJson = await agentsResponse.Content.ReadFromJsonAsync<JsonElement>();
        agentsJson.ValueKind.Should().Be(JsonValueKind.Array);
        var agentNames = agentsJson.EnumerateArray()
            .Select(a => a.GetProperty("name").GetString())
            .ToList();

        agentNames.Should().Contain("assistant",
            "the 'assistant' agent from config should appear in /api/agents (§5)");

        // ── §6: Talk to Your Agent — send message via WebSocket ───────────────
        // The guide shows WebSocket at ws://localhost:18790/ws.
        // We connect, receive "connected", send a message, and verify the response.
        using var ws = new ClientWebSocket();
        using var wsCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws"), wsCts.Token);

        // Receive "connected" confirmation
        var connected = await ReceiveJsonAsync(ws, wsCts.Token);
        connected.GetProperty("type").GetString().Should().Be("connected",
            "WebSocket should send 'connected' on connect (§6)");

        // Send a message addressed to "assistant" (as the guide shows)
        await SendJsonAsync(ws, new
        {
            type = "message",
            content = "Hello from the getting-started test!",
            agent = "assistant"
        }, wsCts.Token);

        // Collect WebSocket messages until we get a "response" type.
        // We may see "activity", "delta", or "error" messages before "response".
        JsonElement responseMsg = default;
        var receivedTypes = new List<string>();
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var incoming = await ReceiveJsonAsync(ws, wsCts.Token);
            if (incoming.ValueKind == JsonValueKind.Undefined) break;

            var type = incoming.TryGetProperty("type", out var t) ? t.GetString() : "unknown";
            receivedTypes.Add(type!);

            if (type == "response")
            {
                responseMsg = incoming;
                break;
            }

            if (type == "error")
            {
                var errContent = incoming.TryGetProperty("content", out var ec) ? ec.GetString() : "unknown error";
                Assert.Fail($"Gateway returned WebSocket error: {errContent}. " +
                    $"Received types: [{string.Join(", ", receivedTypes)}]");
            }
        }

        responseMsg.ValueKind.Should().NotBe(JsonValueKind.Undefined,
            $"should receive a 'response' message from the agent (§6). " +
            $"Received types: [{string.Join(", ", receivedTypes)}]");
        var responseContent = responseMsg.GetProperty("content").GetString();
        responseContent.Should().NotBeNullOrWhiteSpace(
            "agent response should have content (§6)");
        // The fixture provider echoes: "provider[fixture-model]:{user message}"
        responseContent.Should().Contain("Hello from the getting-started test!",
            "fixture provider should echo our message back (§6)");

        if (ws.State == WebSocketState.Open)
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);

        // §5: After first message, workspace should be lazy-created with SOUL.md + IDENTITY.md
        var agentWorkspace = Path.Combine(gw.AgentsPath, "assistant");
        Directory.Exists(agentWorkspace).Should().BeTrue(
            "agent workspace should be created after first message (§5)");
        File.Exists(Path.Combine(agentWorkspace, "SOUL.md")).Should().BeTrue(
            "SOUL.md should exist in agent workspace (§5)");
        File.Exists(Path.Combine(agentWorkspace, "IDENTITY.md")).Should().BeTrue(
            "IDENTITY.md should exist in agent workspace (§5)");

        // ── Final verification: /api/extensions (§8 context) ──────────────────
        var extFinal = await client2.GetFromJsonAsync<JsonElement>("/api/extensions");
        extFinal.GetProperty("healthy").GetBoolean().Should().BeTrue(
            "extensions should report healthy at end of journey");

        // ── Final verification: /api/agents still shows our agent ─────────────
        var agentsFinal = await client2.GetFromJsonAsync<JsonElement>("/api/agents");
        agentsFinal.EnumerateArray()
            .Select(a => a.GetProperty("name").GetString())
            .Should().Contain("assistant",
                "assistant agent should persist through the full journey");
    }

    // ── WebSocket helpers ─────────────────────────────────────────────────────

    private static async Task SendJsonAsync(WebSocket socket, object payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
    }

    private static async Task<JsonElement> ReceiveJsonAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) return default;
            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        if (ms.Length == 0)
            return default;

        using var doc = JsonDocument.Parse(ms.ToArray());
        return doc.RootElement.Clone();
    }

    // ── Config helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Minimal appsettings.json that sets the port but leaves providers/agents empty —
    /// letting config.json (written later) drive the real configuration.
    /// This mirrors the guide: appsettings sets infra, config.json sets user config.
    /// </summary>
    private static string AppSettingsWithPort(int port) => $$"""
        {
          "Logging": { "LogLevel": { "Default": "Information" } },
          "BotNexus": {
            "ExtensionsPath": "~/.botnexus/extensions",
            "Extensions": { "DryRun": false },
            "Providers": {},
            "Channels": { "Instances": {} },
            "Gateway": {
              "Host": "127.0.0.1",
              "Port": {{port}},
              "ApiKey": "",
              "WebSocketEnabled": true,
              "WebSocketPath": "/ws",
              "Heartbeat": { "Enabled": false }
            },
            "Agents": { "Workspace": "~/.botnexus", "Named": {} },
            "Tools": { "McpServers": {}, "Extensions": {} }
          }
        }
        """;
}
