using BotNexus.Agent.Core.Configuration;
using BotNexus.Agent.Core.Types;

namespace BotNexus.CodingAgent.Tests;

public sealed class CodingAgentTests
{
    [Fact]
    public async Task DefaultMessageConverter_FiltersSystemMessages()
    {
        var convertToLlm = DefaultMessageConverter.Create();

        var providerMessages = await convertToLlm([new SystemAgentMessage("[Session context summary: compacted]")], CancellationToken.None);

        providerMessages.ShouldBeEmpty();
    }
}
