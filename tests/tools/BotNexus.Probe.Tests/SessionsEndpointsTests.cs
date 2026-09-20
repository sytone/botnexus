using System.Net;
using System.Reflection;
using BotNexus.Probe;
using BotNexus.Probe.Api;
using BotNexus.Probe.LogIngestion;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;

namespace BotNexus.Probe.Tests;

public sealed class SessionsEndpointsTests
{
    public static TheoryData<string> UnsafeSessionIds => new()
    {
        "..",
        "../outside",
        "..\\outside",
        "/outside",
        "\\outside",
        "C:\\outside",
        "valid%2Foutside",
        "valid%5Coutside"
    };

    [Theory]
    [MemberData(nameof(UnsafeSessionIds))]
    public void FindSessionFile_RejectsUnsafeSessionIdBeforeReading(string sessionId)
    {
        using var root = new TestTempDirectory();
        var sessionsPath = root.File("sessions");
        Directory.CreateDirectory(sessionsPath);
        root.WriteFile("outside.jsonl", "{\"role\":\"user\",\"content\":\"secret\"}");

        var result = InvokeFindSessionFile(sessionsPath, Uri.UnescapeDataString(sessionId));

        result.ShouldBeNull();
    }

    [Fact]
    public void FindSessionFile_DoesNotAcceptSiblingPrefixPath()
    {
        using var root = new TestTempDirectory();
        var sessionsPath = root.File("sessions");
        Directory.CreateDirectory(sessionsPath);
        root.WriteFile("sessions-sibling/secret.jsonl", "{\"content\":\"secret\"}");

        var result = InvokeFindSessionFile(sessionsPath, "../sessions-sibling/secret");

        result.ShouldBeNull();
    }

    [Fact]
    public void FindSessionFile_ReturnsValidExactId()
    {
        using var root = new TestTempDirectory();
        var sessionsPath = root.File("sessions");
        Directory.CreateDirectory(sessionsPath);
        var expected = Path.Combine(sessionsPath, "session-123.jsonl");
        File.WriteAllText(expected, "{}");

        InvokeFindSessionFile(sessionsPath, "session-123").ShouldBe(expected);
    }

    [Fact]
    public void FindSessionFile_PreservesPartialMatchFallback()
    {
        using var root = new TestTempDirectory();
        var sessionsPath = root.File("sessions");
        Directory.CreateDirectory(sessionsPath);
        var expected = Path.Combine(sessionsPath, "prefix-session-123-suffix.jsonl");
        File.WriteAllText(expected, "{}");

        InvokeFindSessionFile(sessionsPath, "session-123").ShouldBe(expected);
    }

    [Fact]
    public async Task SessionDetail_TraversalDoesNotReadOutsideSessionsRoot()
    {
        using var root = new TestTempDirectory();
        var sessionsPath = root.File("sessions");
        Directory.CreateDirectory(sessionsPath);
        root.WriteFile("outside.jsonl", "{\"role\":\"user\",\"content\":\"secret\"}");

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        await using var app = builder.Build();
        var options = new ProbeOptions(5050, null, root.File("logs"), sessionsPath, root.File("sessions.db"), null, false);
        app.MapSessionsEndpoints(options, new JsonlSessionReader(), null);
        await app.StartAsync();

        using var client = app.GetTestClient();
        using var response = await client.GetAsync("/api/sessions/..%5Coutside");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.NotFound);
        body.ShouldNotContain("secret");
    }

    private static string? InvokeFindSessionFile(string sessionsPath, string sessionId)
    {
        var method = typeof(SessionsEndpoints).GetMethod("FindSessionFile", BindingFlags.Static | BindingFlags.NonPublic);
        method.ShouldNotBeNull();
        return (string?)method.Invoke(null, [sessionsPath, sessionId]);
    }
}
