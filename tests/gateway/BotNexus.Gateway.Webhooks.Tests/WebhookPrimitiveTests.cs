using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Webhooks.Tests;

public sealed class WebhookIdTests
{
    [Fact]
    public void Create_ProducesWhPrefix()
    {
        var id = WebhookId.Create();
        Assert.StartsWith("wh_", id.Value);
    }

    [Fact]
    public void Create_ProducesUniqueValues()
    {
        var a = WebhookId.Create();
        var b = WebhookId.Create();
        Assert.NotEqual(a.Value, b.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void From_InvalidValue_Throws(string value)
    {
        Assert.Throws<Vogen.ValueObjectValidationException>(() => WebhookId.From(value));
    }
}

public sealed class WebhookRunIdTests
{
    [Fact]
    public void Create_ProducesWhrPrefix()
    {
        var id = WebhookRunId.Create();
        Assert.StartsWith("whr_", id.Value);
    }

    [Fact]
    public void Create_ProducesUniqueValues()
    {
        var a = WebhookRunId.Create();
        var b = WebhookRunId.Create();
        Assert.NotEqual(a.Value, b.Value);
    }
}
