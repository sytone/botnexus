using System.Diagnostics;
using System.Net;
using System.Net.Http;
using BotNexus.Agent.Providers.Copilot.Headers;

namespace BotNexus.Agent.Providers.Copilot.Tests.Headers;

/// <summary>
/// Pins <see cref="CopilotResponseHeaders.EmitToActivity"/> against the
/// header shape observed in real Copilot CLI captures (mitm flows captured
/// against both Anthropic-flavour and OpenAI-flavour endpoints, Phase 5 / #810).
/// </summary>
public class CopilotResponseHeadersTests
{
    [Fact]
    public void EmitToActivity_PopulatesCorrelationAndQuotaTags()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);

        using var source = new ActivitySource("BotNexus.Test.CopilotResponseHeaders");
        using var activity = source.StartActivity("test", ActivityKind.Client)!;
        activity.ShouldNotBeNull();

        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("")
        };
        response.Headers.TryAddWithoutValidation("x-copilot-service-request-id", "00000000-0000-0000-0000-0000000000aa");
        response.Headers.TryAddWithoutValidation("x-request-id", "00000-aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        response.Headers.TryAddWithoutValidation("X-GitHub-Request-Id", "AAAA:BBBB:CCCCCCC:DDDDDDD:11111111");
        response.Headers.TryAddWithoutValidation("x-copilot-api-exp-assignment-context",
            "e1aaaa1234:1100000;c_x1bbb2345:1200000;");
        response.Headers.TryAddWithoutValidation("x-quota-snapshot-chat",
            "ent=-1&ov=0.0&ovPerm=false&rem=100.0&rst=2099-01-01T00%3A00%3A00Z&totRem=-1");
        response.Headers.TryAddWithoutValidation("x-quota-snapshot-completions",
            "ent=-1&ov=0.0&ovPerm=false&rem=95.5&rst=2099-01-01T00%3A00%3A00Z&totRem=-1");
        response.Headers.TryAddWithoutValidation("x-quota-snapshot-premium_interactions",
            "ent=-1&ov=0.0&ovPerm=true&rem=100.0&rst=2099-01-01T00%3A00%3A00Z&totRem=-1");

        CopilotResponseHeaders.EmitToActivity(response, activity);

        var tags = activity.TagObjects.ToDictionary(t => t.Key, t => t.Value!);
        tags["botnexus.copilot.service_request_id"].ShouldBe("00000000-0000-0000-0000-0000000000aa");
        tags["botnexus.copilot.request_id"].ShouldBe("00000-aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        tags["botnexus.copilot.github_request_id"].ShouldBe("AAAA:BBBB:CCCCCCC:DDDDDDD:11111111");
        tags["botnexus.copilot.exp_assignment_context"].ShouldBe("e1aaaa1234:1100000;c_x1bbb2345:1200000;");

        tags["botnexus.copilot.quota.chat.raw"].ShouldNotBeNull();
        tags["botnexus.copilot.quota.chat.rem"].ShouldBe(100.0);
        tags["botnexus.copilot.quota.chat.ovPerm"].ShouldBe(false);
        tags["botnexus.copilot.quota.chat.ent"].ShouldBe(-1.0);
        tags["botnexus.copilot.quota.chat.totRem"].ShouldBe(-1.0);
        tags["botnexus.copilot.quota.chat.rst"].ShouldBe("2099-01-01T00:00:00Z");
        tags["botnexus.copilot.quota.completions.rem"].ShouldBe(95.5);
        tags["botnexus.copilot.quota.premium_interactions.ovPerm"].ShouldBe(true);
    }

    [Fact]
    public void EmitToActivity_NullActivity_DoesNotThrow()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("")
        };
        response.Headers.TryAddWithoutValidation("x-request-id", "x");

        Should.NotThrow(() => CopilotResponseHeaders.EmitToActivity(response, null));
    }

    [Fact]
    public void EmitToActivity_MissingHeaders_LeavesActivityUntouched()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);
        using var source = new ActivitySource("BotNexus.Test.CopilotResponseHeaders.Empty");
        using var activity = source.StartActivity("test", ActivityKind.Client)!;
        activity.ShouldNotBeNull();

        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("") };

        CopilotResponseHeaders.EmitToActivity(response, activity);

        activity.TagObjects.ShouldBeEmpty();
    }
}
