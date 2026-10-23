using System.Security.Claims;
using System.Text.Json;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Workflows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WorkflowCore.Interface;

namespace BotNexus.Gateway.Workflows.Tests;

public sealed class WorkflowRegistrationTests
{
    [Fact]
    public void Registration_AbsoluteVerifiedHome_DoesNotCreateStoreUntilStarted()
    {
        var root = Path.Combine(Path.GetTempPath(), "botnexus-workflows", Guid.NewGuid().ToString("N"));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEmbeddedWorkflowCore(new TestHome(root));
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IWorkflowHost>().ShouldNotBeNull();
        provider.GetServices<IHostedService>().Count().ShouldBe(1);
        Directory.Exists(root).ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() => services.AddEmbeddedWorkflowCore(new TestHome(root)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative-home")]
    public void Registration_UnverifiedPathShape_IsRejected(string root)
    {
        var services = new ServiceCollection();
        Should.Throw<ArgumentException>(() => services.AddEmbeddedWorkflowCore(new TestHome(root)));
        services.ShouldBeEmpty();
    }

    [Fact]
    public async Task Catalog_ValidatedScopes_OwnActivitiesAndAuthorization()
    {
        var services = new ServiceCollection();
        services.AddScoped<IWorkflowActivity, DisposableActivity>();
        services.AddScoped<IActivityAuthorizer, ScopedAuthorizer>();
        services.AddScoped<IWorkflowInvocationContextResolver, ScopedContextResolver>();
        services.AddTransient<CatalogActivityStep>();
        services.AddWorkflowActivities();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });
        Should.Throw<InvalidOperationException>(() => provider.GetRequiredService<IActivityCatalog>());
        Should.Throw<InvalidOperationException>(() => provider.GetRequiredService<CatalogActivityStep>());

        DisposableActivity firstActivity;
        ScopedAuthorizer firstAuthorizer;
        ScopedContextResolver firstResolver;
        using (var first = provider.CreateScope())
        {
            var catalog = first.ServiceProvider.GetRequiredService<IActivityCatalog>();
            catalog.ShouldBeSameAs(first.ServiceProvider.GetRequiredService<IActivityCatalog>());
            firstAuthorizer = first.ServiceProvider.GetRequiredService<IActivityAuthorizer>().ShouldBeOfType<ScopedAuthorizer>();
            firstResolver = first.ServiceProvider.GetRequiredService<IWorkflowInvocationContextResolver>().ShouldBeOfType<ScopedContextResolver>();
            var step = first.ServiceProvider.GetRequiredService<CatalogActivityStep>();
            step.ActivityName = "test.scoped";
            step.ActivityVersion = 1;
            await step.RunAsync(new WorkflowCore.Models.StepExecutionContext
            {
                Workflow = new WorkflowCore.Models.WorkflowInstance { Id = "scoped-instance" }
            });
            firstResolver.Calls.ShouldBe(1);
            firstActivity = firstAuthorizer.Activity.ShouldBeOfType<DisposableActivity>();
            firstActivity.Calls.ShouldBe(1);
            firstActivity.Disposed.ShouldBeFalse();
            firstAuthorizer.Disposed.ShouldBeFalse();
            firstResolver.Disposed.ShouldBeFalse();
        }
        firstActivity.Disposed.ShouldBeTrue();
        firstAuthorizer.Disposed.ShouldBeTrue();
        firstResolver.Disposed.ShouldBeTrue();

        DisposableActivity secondActivity;
        ScopedAuthorizer secondAuthorizer;
        using (var second = provider.CreateScope())
        {
            var catalog = second.ServiceProvider.GetRequiredService<IActivityCatalog>();
            secondAuthorizer = second.ServiceProvider.GetRequiredService<IActivityAuthorizer>().ShouldBeOfType<ScopedAuthorizer>();
            await catalog.InvokeAsync("test.scoped", 1, new ActivityInvocationContext(new ClaimsPrincipal(), "second"),
                JsonSerializer.SerializeToElement(1));
            secondActivity = secondAuthorizer.Activity.ShouldBeOfType<DisposableActivity>();
            secondActivity.ShouldNotBeSameAs(firstActivity);
            secondAuthorizer.ShouldNotBeSameAs(firstAuthorizer);
            secondActivity.Disposed.ShouldBeFalse();
            secondAuthorizer.Disposed.ShouldBeFalse();
        }
        secondActivity.Disposed.ShouldBeTrue();
        secondAuthorizer.Disposed.ShouldBeTrue();
        Should.Throw<InvalidOperationException>(() => provider.GetRequiredService<IActivityCatalog>());
    }

    [Fact]
    public async Task Catalog_TransientActivity_IsRetainedOnlyForItsExecutionScope()
    {
        var activities = new List<DisposableActivity>();
        var services = new ServiceCollection();
        services.AddTransient<IWorkflowActivity>(_ =>
        {
            var activity = new DisposableActivity();
            activities.Add(activity);
            return activity;
        });
        services.AddScoped<IActivityAuthorizer, Permit>();
        services.AddWorkflowActivities();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });
        Should.Throw<InvalidOperationException>(() => provider.GetRequiredService<IActivityCatalog>());
        activities.ShouldBeEmpty();
        var context = new ActivityInvocationContext(new ClaimsPrincipal(), "transient");
        using (var first = provider.CreateScope())
        {
            var catalog = first.ServiceProvider.GetRequiredService<IActivityCatalog>();
            await catalog.InvokeAsync("test.scoped", 1, context, JsonSerializer.SerializeToElement(1));
            await catalog.InvokeAsync("test.scoped", 1, context, JsonSerializer.SerializeToElement(2));
            activities.Count.ShouldBe(1);
            activities[0].Calls.ShouldBe(2);
            activities[0].Disposed.ShouldBeFalse();
        }
        activities[0].Disposed.ShouldBeTrue();
        using (var second = provider.CreateScope())
        {
            var catalog = second.ServiceProvider.GetRequiredService<IActivityCatalog>();
            await catalog.InvokeAsync("test.scoped", 1, context, JsonSerializer.SerializeToElement(3));
            activities.Count.ShouldBe(2);
            activities[1].ShouldNotBeSameAs(activities[0]);
            activities[1].Calls.ShouldBe(1);
            activities[1].Disposed.ShouldBeFalse();
        }
        activities[1].Disposed.ShouldBeTrue();
    }

    private sealed class Permit : IActivityAuthorizer
    {
        public ValueTask<bool> AuthorizeAsync(ActivityDescriptor activity, ActivityInvocationContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(true);
    }

    private sealed class DisposableActivity : IWorkflowActivity, IDisposable
    {
        public ActivityDescriptor Descriptor { get; } = new("test.scoped", 1);
        public int Calls { get; private set; }
        public bool Disposed { get; private set; }
        public ValueTask<JsonElement> InvokeAsync(ActivityInvocationContext context, JsonElement input, CancellationToken cancellationToken)
        {
            Disposed.ShouldBeFalse();
            Calls++;
            return ValueTask.FromResult(input);
        }
        public void Dispose() => Disposed = true;
    }

    private sealed class ScopedAuthorizer(IEnumerable<IWorkflowActivity> activities) : IActivityAuthorizer, IDisposable
    {
        private readonly IWorkflowActivity[] _activities = activities.ToArray();
        public IWorkflowActivity? Activity { get; private set; }
        public bool Disposed { get; private set; }
        public ValueTask<bool> AuthorizeAsync(ActivityDescriptor activity, ActivityInvocationContext context, CancellationToken cancellationToken)
        {
            Disposed.ShouldBeFalse();
            Activity = _activities.Single();
            return ValueTask.FromResult(true);
        }
        public void Dispose() => Disposed = true;
    }

    private sealed class ScopedContextResolver : IWorkflowInvocationContextResolver, IDisposable
    {
        public int Calls { get; private set; }
        public bool Disposed { get; private set; }
        public ValueTask<ActivityInvocationContext?> ResolveAsync(string workflowInstanceId, CancellationToken cancellationToken)
        {
            Disposed.ShouldBeFalse();
            Calls++;
            return ValueTask.FromResult<ActivityInvocationContext?>(new(new ClaimsPrincipal(), workflowInstanceId));
        }
        public void Dispose() => Disposed = true;
    }

    private sealed record TestHome(string RootPath) : IVerifiedHome { public string? WorldId => "test"; }
}
