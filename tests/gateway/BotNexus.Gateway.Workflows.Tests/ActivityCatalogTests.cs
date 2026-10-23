using System.Security.Claims;
using System.Text.Json;
using BotNexus.Gateway.Abstractions.Workflows;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Gateway.Workflows.Tests;

public sealed class ActivityCatalogTests
{
    private static readonly ActivityInvocationContext Context = new(new ClaimsPrincipal(new ClaimsIdentity()), "test-run");

    [Fact]
    public void Construction_DuplicateNameAndVersion_RejectsRegistration()
    {
        var action = new EchoActivity();
        Should.Throw<ArgumentException>(() => new ActivityCatalog([action, action], new Permit()));
    }

    [Theory]
    [InlineData("echo", 1)]
    [InlineData("test.echo", 0)]
    [InlineData("", 1)]
    public void Descriptor_InvalidIdentity_RejectsRegistration(string name, int version)
        => Should.Throw<ArgumentException>(() => new ActivityDescriptor(name, version));

    [Fact]
    public async Task Invoke_UnknownVersion_DoesNotCallApplicationCode()
    {
        var action = new EchoActivity();
        var catalog = new ActivityCatalog([action], new Permit());
        await Should.ThrowAsync<KeyNotFoundException>(() => catalog.InvokeAsync("test.echo", 2, Context, JsonSerializer.SerializeToElement(1)).AsTask());
        action.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Invoke_Denied_DoesNotCallApplicationCode()
    {
        var action = new EchoActivity();
        var catalog = new ActivityCatalog([action], new DenyActivityAuthorizer());
        await Should.ThrowAsync<UnauthorizedAccessException>(() => catalog.InvokeAsync("test.echo", 1, Context, JsonSerializer.SerializeToElement(1)).AsTask());
        action.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Invoke_InputAndOutputLimits_AreEnforced()
    {
        var action = new EchoActivity();
        var catalog = new ActivityCatalog([action], new Permit());
        await Should.ThrowAsync<ArgumentException>(() => catalog.InvokeAsync("test.echo", 1, Context, JsonSerializer.SerializeToElement(new string('x', 100))).AsTask());
        action.Calls.ShouldBe(0);
        action.OversizedOutput = true;
        await Should.ThrowAsync<ArgumentException>(() => catalog.InvokeAsync("test.echo", 1, Context, JsonSerializer.SerializeToElement(1)).AsTask());
        action.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task Invoke_DisposedCallerAndApplicationDocuments_ResultRemainsIsolated()
    {
        var action = new EchoActivity();
        var catalog = new ActivityCatalog([action], new Permit());
        JsonElement result;
        using (var input = JsonDocument.Parse("42"))
            result = await catalog.InvokeAsync("test.echo", 1, Context, input.RootElement);
        action.SeenInput.GetInt32().ShouldBe(42);
        action.OutputDocument?.Dispose();
        result.GetInt32().ShouldBe(43);
    }

    [Fact]
    public async Task Registration_NoPolicy_DefaultsToDeny()
    {
        var services = new ServiceCollection();
        services.AddWorkflowActivities();
        services.AddSingleton<IWorkflowActivity, EchoActivity>();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        await Should.ThrowAsync<UnauthorizedAccessException>(() => scope.ServiceProvider.GetRequiredService<IActivityCatalog>()
            .InvokeAsync("test.echo", 1, Context, JsonSerializer.SerializeToElement(1)).AsTask());
    }

    [Fact]
    public async Task Invoke_Cancelled_DoesNotCallApplicationCode()
    {
        var action = new EchoActivity();
        var catalog = new ActivityCatalog([action], new Permit());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => catalog.InvokeAsync("test.echo", 1, Context,
            JsonSerializer.SerializeToElement(1), cancellation.Token).AsTask());
        action.Calls.ShouldBe(0);
    }

    private sealed class Permit : IActivityAuthorizer
    {
        public ValueTask<bool> AuthorizeAsync(ActivityDescriptor activity, ActivityInvocationContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(true);
    }

    private sealed class EchoActivity : IWorkflowActivity
    {
        public ActivityDescriptor Descriptor { get; } = new("test.echo", 1, 32, 32);
        public int Calls { get; private set; }
        public bool OversizedOutput { get; set; }
        public JsonElement SeenInput { get; private set; }
        public JsonDocument? OutputDocument { get; private set; }
        public ValueTask<JsonElement> InvokeAsync(ActivityInvocationContext context, JsonElement input, CancellationToken cancellationToken)
        {
            Calls++;
            SeenInput = input;
            OutputDocument = JsonDocument.Parse(OversizedOutput ? JsonSerializer.Serialize(new string('x', 100)) : (input.GetInt32() + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
            return ValueTask.FromResult(OutputDocument.RootElement);
        }
    }
}
