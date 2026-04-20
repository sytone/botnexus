using BotNexus.Cli.Wizard;
using BotNexus.Cli.Wizard.Steps;

namespace BotNexus.Cli.Tests.Wizard;

public class WizardContextTests
{
    [Fact]
    public void Set_and_Get_roundtrips_value()
    {
        var ctx = new WizardContext();
        ctx.Set("name", "hello");
        ctx.Get<string>("name").ShouldBe("hello");
    }

    [Fact]
    public void Get_throws_for_missing_key()
    {
        var ctx = new WizardContext();
        Should.Throw<KeyNotFoundException>(() => ctx.Get<string>("missing"));
    }

    [Fact]
    public void TryGet_returns_false_for_missing_key()
    {
        var ctx = new WizardContext();
        ctx.TryGet<string>("missing", out _).ShouldBeFalse();
    }

    [Fact]
    public void TryGet_returns_true_and_value_when_present()
    {
        var ctx = new WizardContext();
        ctx.Set("count", 42);
        ctx.TryGet<int>("count", out var value).ShouldBeTrue();
        value.ShouldBe(42);
    }

    [Fact]
    public void Has_returns_true_when_key_exists()
    {
        var ctx = new WizardContext();
        ctx.Set("x", "y");
        ctx.Has("x").ShouldBeTrue();
    }

    [Fact]
    public void Has_returns_false_when_key_missing()
    {
        var ctx = new WizardContext();
        ctx.Has("x").ShouldBeFalse();
    }

    [Fact]
    public void Remove_deletes_key()
    {
        var ctx = new WizardContext();
        ctx.Set("x", "y");
        ctx.Remove("x");
        ctx.Has("x").ShouldBeFalse();
    }

    [Fact]
    public void Keys_are_case_insensitive()
    {
        var ctx = new WizardContext();
        ctx.Set("Name", "value");
        ctx.Get<string>("name").ShouldBe("value");
        ctx.Get<string>("NAME").ShouldBe("value");
    }
}
