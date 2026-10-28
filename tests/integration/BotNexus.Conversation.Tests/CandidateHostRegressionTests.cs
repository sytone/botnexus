using System.Net;

namespace BotNexus.Conversation.Tests;

[Collection("CandidateGateway")]
public sealed class CandidateHostRegressionTests(CandidateGatewayFixture fixture)
{
    [Fact]
    public async Task CandidateHost_UsesInProcessTransport_AndServesCandidateAssembly()
    {
        // RED against the former fixture: it dialled ambient localhost:5006 and could skip
        // every scenario. This assertion must run even when no developer gateway exists.
        fixture.Http.BaseAddress.ShouldBe(new Uri("http://candidate.test"));
        using var response = await fixture.Http.GetAsync("/api/version");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("commit");
        fixture.Factory.Server.ShouldNotBeNull();
    }
}
