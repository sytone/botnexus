using System.Security.Claims;
using System.Text.Json;
using BotNexus.Gateway.Abstractions.Workflows;
using Microsoft.Extensions.DependencyInjection;
using WorkflowCore.Interface;
using WorkflowCore.Models;

namespace BotNexus.Gateway.Workflows.Tests;

public sealed class ActivityBoundaryTests
{
    private static ActivityInvocationContext Context => new(new ClaimsPrincipal(), "instance");

    [Fact]
    public async Task Catalog_DeclaredShape_RejectsInputBeforeApplicationAndRejectsInvalidOutput()
    {
        var action = new ShapedActivity();
        var catalog = new ActivityCatalog([action], new Permit());
        await Should.ThrowAsync<ArgumentException>(() => catalog.InvokeAsync("test.shape", 1, Context,
            JsonSerializer.SerializeToElement(42)).AsTask());
        action.Calls.ShouldBe(0);
        action.InvalidOutput = true;
        await Should.ThrowAsync<ArgumentException>(() => catalog.InvokeAsync("test.shape", 1, Context,
            JsonSerializer.SerializeToElement(new { value = 1 })).AsTask());
        action.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task Catalog_UndefinedJson_IsNotAValidPayload()
    {
        var action = new ShapedActivity();
        var catalog = new ActivityCatalog([action], new Permit());
        await Should.ThrowAsync<ArgumentException>(() => catalog.InvokeAsync("test.shape", 1, Context, default).AsTask());
        action.Calls.ShouldBe(0);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(1048577, 1)]
    public void Descriptor_InvalidSize_RejectsContract(int input, int output)
        => Should.Throw<ArgumentException>(() => new ActivityDescriptor("test.shape", 1, input, output));

    [Fact]
    public async Task Bridge_RuntimeBindingForAnotherInstance_IsRejected()
    {
        var step = new CatalogActivityStep(new ActivityCatalog([], new Permit()), new WrongIdentity());
        await Should.ThrowAsync<UnauthorizedAccessException>(() => step.RunAsync(new StepExecutionContext
        {
            Workflow = new WorkflowInstance { Id = "instance" }
        }));
    }

    [Fact]
    public async Task Bridge_CancelledEngineToken_DoesNotResolveIdentity()
    {
        var identity = new WrongIdentity();
        var step = new CatalogActivityStep(new ActivityCatalog([], new Permit()), identity);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => step.RunAsync(new StepExecutionContext
        {
            Workflow = new WorkflowInstance { Id = "instance" }, CancellationToken = cancellation.Token
        }));
        identity.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task SafetyMiddleware_CatalogStep_OverridesAutomaticRetryWithSuspend()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWorkflow();
        using var provider = services.BuildServiceProvider();
        var builder = provider.GetRequiredService<IWorkflowBuilder>().UseData<object>();
        builder.StartWith<CatalogActivityStep>().OnError(WorkflowErrorHandling.Retry);
        var step = builder.Build("safety", 1).Steps.First();
        var context = new StepExecutionContext { Step = step };
        var body = new CatalogActivityStep(new ActivityCatalog([], new Permit()), new WrongIdentity());
        var middleware = new CatalogActivitySafetyMiddleware();
        await Should.ThrowAsync<InvalidOperationException>(() => middleware.HandleAsync(context, body,
            () => throw new InvalidOperationException("uncertain side effect")));
        step.ErrorBehavior.ShouldBe(WorkflowErrorHandling.Suspend);
    }

    private sealed class Permit : IActivityAuthorizer
    {
        public ValueTask<bool> AuthorizeAsync(ActivityDescriptor activity, ActivityInvocationContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(true);
    }

    private sealed class WrongIdentity : IWorkflowInvocationContextResolver
    {
        public int Calls { get; private set; }
        public ValueTask<ActivityInvocationContext?> ResolveAsync(string workflowInstanceId, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult<ActivityInvocationContext?>(new(new ClaimsPrincipal(), "another-instance"));
        }
    }

    private sealed class ShapedActivity : IWorkflowActivity
    {
        public ActivityDescriptor Descriptor { get; } = new("test.shape", 1, inputKind: JsonValueKind.Object, outputKind: JsonValueKind.Number);
        public int Calls { get; private set; }
        public bool InvalidOutput { get; set; }
        public ValueTask<JsonElement> InvokeAsync(ActivityInvocationContext context, JsonElement input, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(InvalidOutput ? JsonSerializer.SerializeToElement("invalid") : JsonSerializer.SerializeToElement(1));
        }
    }
}
