using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BotNexus.Tests.Deployment.Infrastructure;

namespace BotNexus.Tests.Deployment.Tests;

/// <summary>SC-DPL-005: Restart — sessions restored from disk, conversation context intact.</summary>
[Trait("Category", "Deployment")]
public sealed class RestartPersistenceTests
{
    [Fact]
    public async Task Restart_SessionsRestoredFromDisk()
    {
        var port = GatewayProcessFixture.FindFreePort();
        await using var gw = new GatewayProcessFixture(port);

        gw.WriteAppSettings(GatewayProcessFixture.DefaultAppSettings(port));
        gw.WriteConfigJson(GatewayProcessFixture.MinimalConfigJson());

        // Seed sessions
        gw.SeedSession("persist-sess-1",
            ("User", "Remember the code ALPHA-42."),
            ("Assistant", "Got it — I'll remember ALPHA-42."));
        gw.SeedSession("persist-sess-2",
            ("User", "Save this: meeting at 3pm."),
            ("Assistant", "Noted: meeting at 3pm."),
            ("User", "What did I save?"),
            ("Assistant", "You saved: meeting at 3pm."));

        // --- First launch ---
        await gw.StartAsync();
        await gw.WaitForHealthyAsync();

        using var client1 = gw.CreateHttpClient();
        var resp1 = await client1.GetAsync("/api/sessions");
        resp1.StatusCode.Should().Be(HttpStatusCode.OK);
        var sess1 = await resp1.Content.ReadFromJsonAsync<JsonElement>();
        var count1 = sess1.GetArrayLength();
        count1.Should().BeGreaterThanOrEqualTo(2, "both seeded sessions should load on first start");

        // Verify session detail shows correct message count
        var detail1 = await client1.GetAsync("/api/sessions/persist-sess-1");
        detail1.StatusCode.Should().Be(HttpStatusCode.OK);
        var detailJson1 = await detail1.Content.ReadFromJsonAsync<JsonElement>();
        detailJson1.GetProperty("history").GetArrayLength().Should().Be(2, "session 1 should have 2 entries");

        var detail2 = await client1.GetAsync("/api/sessions/persist-sess-2");
        detail2.StatusCode.Should().Be(HttpStatusCode.OK);
        var detailJson2 = await detail2.Content.ReadFromJsonAsync<JsonElement>();
        detailJson2.GetProperty("history").GetArrayLength().Should().Be(4, "session 2 should have 4 entries");

        // --- Stop ---
        await gw.StopAsync();
        gw.IsRunning.Should().BeFalse();

        // --- Restart ---
        await gw.StartAsync();
        await gw.WaitForHealthyAsync();

        using var client2 = gw.CreateHttpClient();

        // Verify sessions survived restart
        var resp2 = await client2.GetAsync("/api/sessions");
        resp2.StatusCode.Should().Be(HttpStatusCode.OK);
        var sess2 = await resp2.Content.ReadFromJsonAsync<JsonElement>();
        sess2.GetArrayLength().Should().Be(count1, "same sessions should be available after restart");

        // Verify conversation history is intact
        var detail3 = await client2.GetAsync("/api/sessions/persist-sess-2");
        detail3.StatusCode.Should().Be(HttpStatusCode.OK);
        var detailJson3 = await detail3.Content.ReadFromJsonAsync<JsonElement>();
        detailJson3.GetProperty("history").GetArrayLength().Should().Be(4, "history should survive restart");

        // Verify content is correct
        var history = detailJson3.GetProperty("history").EnumerateArray().ToList();
        history[0].GetProperty("content").GetString().Should().Contain("meeting at 3pm");
    }
}
