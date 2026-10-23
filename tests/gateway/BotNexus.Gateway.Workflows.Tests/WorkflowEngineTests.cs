using System.Security.Claims;
using System.Text.Json;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Workflows;
using BotNexus.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WorkflowCore.Interface;
using WorkflowCore.Models;

namespace BotNexus.Gateway.Workflows.Tests;

public sealed class WorkflowEngineTests
{
    [Fact]
    public async Task Engine_ActivityResult_BranchLoopAndWait_SurviveSqliteReopen()
    {
        var root = Path.Combine(Path.GetTempPath(), "botnexus-workflows", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string instanceId;
        try
        {
            using (var first = CreateProvider(root))
            {
                var host = first.GetRequiredService<IWorkflowHost>();
                host.RegisterWorkflow<FoundationWorkflow, RunData>();
                await StartAsync(first);
                try
                {
                    instanceId = await host.StartWorkflow("foundation-test", 1, new RunData());
                    await TestAwait.EventuallyAsync(async () =>
                    {
                        var subscriptions = await host.PersistenceStore.GetSubscriptions("foundation.release", instanceId, DateTime.UtcNow);
                        return subscriptions.Any();
                    }, "durable engine event subscription");
                    var persisted = await host.PersistenceStore.GetWorkflowInstance(instanceId);
                    var data = persisted.Data.ShouldBeOfType<RunData>();
                    data.Count.ShouldBe(3);
                    data.BranchTaken.ShouldBeTrue();
                    data.Released.ShouldBeFalse();
                    File.Exists(Path.Combine(root, "workflows", "workflow-core.sqlite")).ShouldBeTrue();
                }
                finally { await StopAsync(first); }
            }

            using (var reopened = CreateProvider(root))
            {
                var host = reopened.GetRequiredService<IWorkflowHost>();
                host.RegisterWorkflow<FoundationWorkflow, RunData>();
                await StartAsync(reopened);
                try
                {
                    await host.PublishEvent("foundation.release", instanceId, "released");
                    await TestAwait.EventuallyAsync(async () =>
                        (await host.PersistenceStore.GetWorkflowInstance(instanceId)).Status == WorkflowStatus.Complete,
                        "reopened workflow completion");
                    var persisted = await host.PersistenceStore.GetWorkflowInstance(instanceId);
                    var data = persisted.Data.ShouldBeOfType<RunData>();
                    data.Released.ShouldBeTrue();
                    data.Count.ShouldBe(4);
                    data.BranchTaken.ShouldBeTrue();
                }
                finally { await StopAsync(reopened); }
            }
        }
        finally
        {
            SqlitePoolCleanup.ClearPoolsUnder(root);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Engine_ActivityError_SuspendsDespiteDefinitionRetryPolicy()
    {
        var root = Path.Combine(Path.GetTempPath(), "botnexus-workflows", Guid.NewGuid().ToString("N"));
        try
        {
            using var provider = CreateProvider(root);
            var host = provider.GetRequiredService<IWorkflowHost>();
            host.RegisterWorkflow<FailureWorkflow, RunData>();
            await StartAsync(provider);
            try
            {
                var instanceId = await host.StartWorkflow("foundation-failure", 1, new RunData());
                await TestAwait.EventuallyAsync(async () =>
                    (await host.PersistenceStore.GetWorkflowInstance(instanceId)).Status == WorkflowStatus.Suspended,
                    "catalog failure persisted as suspended rather than retrying");
                var persisted = await host.PersistenceStore.GetWorkflowInstance(instanceId);
                persisted.Data.ShouldBeOfType<RunData>().Count.ShouldBe(0);
            }
            finally { await StopAsync(provider); }
        }
        finally
        {
            SqlitePoolCleanup.ClearPoolsUnder(root);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Bridge_NoRuntimeIdentity_FailsClosedWithoutCallingActivity()
    {
        var action = new IncrementActivity();
        var step = new CatalogActivityStep(new ActivityCatalog([action], new Permit()), new DenyWorkflowInvocationContextResolver())
        {
            ActivityName = "test.increment", ActivityVersion = 1, InputJson = "0"
        };
        await Should.ThrowAsync<UnauthorizedAccessException>(() => step.RunAsync(new StepExecutionContext
        {
            Workflow = new WorkflowInstance { Id = "unbound-instance" }
        }));
        action.Calls.ShouldBe(0);
    }

    [Fact]
    public void Registration_IsOptIn_RequiresVerifiedHome()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWorkflowActivities();
        using var provider = services.BuildServiceProvider();
        provider.GetService<IWorkflowHost>().ShouldBeNull();
        provider.GetServices<IHostedService>().ShouldBeEmpty();
    }

    private static ServiceProvider CreateProvider(string root)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IActivityAuthorizer, Permit>();
        services.AddSingleton<IWorkflowInvocationContextResolver, TestIdentityResolver>();
        services.AddSingleton<IWorkflowActivity, IncrementActivity>();
        services.AddEmbeddedWorkflowCore(new TestHome(root));
        return services.BuildServiceProvider();
    }

    private static async Task StartAsync(ServiceProvider provider)
    {
        foreach (var service in provider.GetServices<IHostedService>()) await service.StartAsync(CancellationToken.None);
    }

    private static async Task StopAsync(ServiceProvider provider)
    {
        foreach (var service in provider.GetServices<IHostedService>().Reverse()) await service.StopAsync(CancellationToken.None);
    }

    private sealed record TestHome(string RootPath) : IVerifiedHome { public string? WorldId => "test-world"; }
    private sealed class Permit : IActivityAuthorizer
    {
        public ValueTask<bool> AuthorizeAsync(ActivityDescriptor activity, ActivityInvocationContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(context.Principal.Identity?.IsAuthenticated == true);
    }
    private sealed class TestIdentityResolver : IWorkflowInvocationContextResolver
    {
        public ValueTask<ActivityInvocationContext?> ResolveAsync(string workflowInstanceId, CancellationToken cancellationToken)
            => ValueTask.FromResult<ActivityInvocationContext?>(new(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "test-actor")], "test")), workflowInstanceId));
    }
    private sealed class IncrementActivity : IWorkflowActivity
    {
        public ActivityDescriptor Descriptor { get; } = new("test.increment", 1);
        public int Calls { get; private set; }
        public ValueTask<JsonElement> InvokeAsync(ActivityInvocationContext context, JsonElement input, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(JsonSerializer.SerializeToElement(input.GetInt32() + 1));
        }
    }

    public sealed class RunData
    {
        public int Count { get; set; }
        public bool BranchTaken { get; set; }
        public bool Released { get; set; }
    }

    public sealed class FailureWorkflow : IWorkflow<RunData>
    {
        public string Id => "foundation-failure";
        public int Version => 1;
        public void Build(IWorkflowBuilder<RunData> builder)
            => builder.StartWith<CatalogActivityStep>()
                .Input(step => step.ActivityName, _ => "test.increment")
                .Input(step => step.ActivityVersion, _ => 1)
                .Input(step => step.InputJson, _ => "\"not-a-number\"")
                .OnError(WorkflowErrorHandling.Retry);
    }

    public sealed class FoundationWorkflow : IWorkflow<RunData>
    {
        public string Id => "foundation-test";
        public int Version => 1;
        public void Build(IWorkflowBuilder<RunData> builder)
        {
            builder.UseDefaultErrorBehavior(WorkflowErrorHandling.Suspend);
            builder.StartWith<CatalogActivityStep>()
                .Input(step => step.ActivityName, _ => "test.increment")
                .Input(step => step.ActivityVersion, _ => 1)
                .Input(step => step.InputJson, _ => "0")
                .Output(data => data.Count, step => int.Parse(step.OutputJson, System.Globalization.CultureInfo.InvariantCulture))
                .If(data => data.Count == 1).Do(branch => branch
                    .StartWith(context => ((RunData)context.Workflow.Data).BranchTaken = true))
                .While(data => data.Count < 3).Do(loop => loop
                    .StartWith<CatalogActivityStep>()
                    .Input(step => step.ActivityName, _ => "test.increment")
                    .Input(step => step.ActivityVersion, _ => 1)
                    .Input(step => step.InputJson, data => data.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .Output(data => data.Count, step => int.Parse(step.OutputJson, System.Globalization.CultureInfo.InvariantCulture)))
                .WaitFor("foundation.release", (data, context) => context.Workflow.Id)
                .Then<CatalogActivityStep>()
                .Input(step => step.ActivityName, _ => "test.increment")
                .Input(step => step.ActivityVersion, _ => 1)
                .Input(step => step.InputJson, data => data.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Output(data => data.Count, step => int.Parse(step.OutputJson, System.Globalization.CultureInfo.InvariantCulture))
                .Then(context => ((RunData)context.Workflow.Data).Released = true);
        }
    }
}
