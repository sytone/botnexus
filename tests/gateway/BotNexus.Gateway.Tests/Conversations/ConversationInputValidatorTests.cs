using BotNexus.Gateway.Conversations;
using Xunit;

namespace BotNexus.Gateway.Tests.Conversations;

public sealed class ConversationInputValidatorTests
{
    [Fact]
    public void ValidateTitle_Null_NotRequired_ReturnsNull()
    {
        Assert.Null(ConversationInputValidator.ValidateTitle(null, required: false));
    }

    [Fact]
    public void ValidateTitle_Null_Required_ReturnsError()
    {
        var error = ConversationInputValidator.ValidateTitle(null, required: true);
        Assert.NotNull(error);
        Assert.Contains("required", error);
    }

    [Fact]
    public void ValidateTitle_Empty_Required_ReturnsError()
    {
        var error = ConversationInputValidator.ValidateTitle("   ", required: true);
        Assert.NotNull(error);
        Assert.Contains("empty", error);
    }

    [Fact]
    public void ValidateTitle_ExceedsMaxLength_ReturnsError()
    {
        var longTitle = new string('x', 201);
        var error = ConversationInputValidator.ValidateTitle(longTitle);
        Assert.NotNull(error);
        Assert.Contains("200", error);
    }

    [Fact]
    public void ValidateTitle_AtMaxLength_ReturnsNull()
    {
        var title = new string('x', 200);
        Assert.Null(ConversationInputValidator.ValidateTitle(title));
    }

    [Fact]
    public void ValidateTitle_TrimsWhitespaceBeforeValidation()
    {
        // 200 chars + spaces = over raw length, but trimmed is exactly 200
        var title = "  " + new string('x', 200) + "  ";
        Assert.Null(ConversationInputValidator.ValidateTitle(title));
    }

    [Fact]
    public void ValidatePurpose_Null_ReturnsNull()
    {
        Assert.Null(ConversationInputValidator.ValidatePurpose(null));
    }

    [Fact]
    public void ValidatePurpose_ExceedsMaxLength_ReturnsError()
    {
        var longPurpose = new string('y', 1001);
        var error = ConversationInputValidator.ValidatePurpose(longPurpose);
        Assert.NotNull(error);
        Assert.Contains("1000", error);
    }

    [Fact]
    public void ValidatePurpose_AtMaxLength_ReturnsNull()
    {
        var purpose = new string('y', 1000);
        Assert.Null(ConversationInputValidator.ValidatePurpose(purpose));
    }

    [Fact]
    public void ValidateInstructions_Null_ReturnsNull()
    {
        Assert.Null(ConversationInputValidator.ValidateInstructions(null));
    }

    [Fact]
    public void ValidateInstructions_ExceedsMaxLength_ReturnsError()
    {
        var longInstructions = new string('z', 10_001);
        var error = ConversationInputValidator.ValidateInstructions(longInstructions);
        Assert.NotNull(error);
        Assert.Contains("10000", error);
    }

    [Fact]
    public void ValidateInstructions_AtMaxLength_ReturnsNull()
    {
        var instructions = new string('z', 10_000);
        Assert.Null(ConversationInputValidator.ValidateInstructions(instructions));
    }
}
