using BotNexus.Agent.Core.ExtensionPoints.Messages;
using BotNexus.Agent.Core.Types;

namespace BotNexus.CodingAgent.Tests;

public sealed class CodingAgentTests
{
    [Fact]
    public async Task DefaultProviderMessageTransformer_FiltersSystemMessages()
    {
        var convertToLlm = DefaultProviderMessageTransformer.Create();

        var providerMessages = await convertToLlm([new SystemAgentMessage("[Session context summary: compacted]")], CancellationToken.None);

        providerMessages.ShouldBeEmpty();
    }
}
