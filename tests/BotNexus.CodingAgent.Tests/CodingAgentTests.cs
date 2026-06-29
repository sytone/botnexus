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

    [Fact]
    public async Task DefaultMessageConverter_KeepsCompactionSummaryWhenUserRole()
    {
        // #1694: a manual /compact emits the summary as a User message (the
        // [CONTEXT COMPACTION -- REFERENCE ONLY] wrapper is already in content), so it
        // must survive conversion and reach the provider instead of being dropped.
        var convertToLlm = DefaultMessageConverter.Create();
        var providerMessages = await convertToLlm([new UserMessage("[CONTEXT COMPACTION -- REFERENCE ONLY] compacted")], CancellationToken.None);
        providerMessages.ShouldHaveSingleItem();
    }
}
