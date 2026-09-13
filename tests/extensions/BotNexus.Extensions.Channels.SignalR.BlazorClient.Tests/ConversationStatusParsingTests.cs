using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class ConversationStatusParsingTests
{
    [Theory]
    [InlineData("Active", ConversationStatus.Active, true)]
    [InlineData("active", ConversationStatus.Active, true)]
    [InlineData("ACTIVE", ConversationStatus.Active, true)]
    [InlineData("Archived", ConversationStatus.Archived, false)]
    [InlineData("", ConversationStatus.Active, true)]
    [InlineData(null, ConversationStatus.Active, true)]
    [InlineData("Suspended", ConversationStatus.Active, true)]
    [InlineData("7", ConversationStatus.Active, true)]
    public void Parse_IsTotalAndIsActiveUsesTheSameTolerantRule(
        string? wireValue,
        ConversationStatus expected,
        bool expectedIsActive)
    {
        Assert.Equal(expected, ConversationStatusParsing.Parse(wireValue));
        Assert.Equal(expectedIsActive, ConversationStatusParsing.IsActive(wireValue));
    }
}
