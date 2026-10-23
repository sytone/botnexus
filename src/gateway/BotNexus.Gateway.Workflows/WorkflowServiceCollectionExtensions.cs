using System.IO.Abstractions;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Workflows;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using WorkflowCore.Interface;
using WorkflowCore.Persistence.EntityFramework.Services;
using WorkflowCore.Persistence.Sqlite;

namespace BotNexus.Gateway.Workflows;

/// <summary>Opt-in registration of activity services and a dedicated single-node embedded engine.</summary>
public static class WorkflowServiceCollectionExtensions
{
    /// <summary>Registers a scoped catalog and stateless singleton deny-by-default policy services, without starting an engine.</summary>
    /// <param name="services">The collection to configure; existing catalog and policy registrations are preserved.</param>
    /// <returns>The same collection for chaining.</returns>
    /// <remarks>
    /// Resolve the catalog from an execution scope, never the root provider. That scope owns disposable scoped
    /// and transient activities and policies. Singleton contributions must be thread-safe; a scoped catalog does
    /// not serialize concurrent calls. Extensions contribute IWorkflowActivity services through IServiceContributor.
    /// </remarks>
    public static IServiceCollection AddWorkflowActivities(this IServiceCollection services)
    {
        services.TryAddSingleton<IActivityAuthorizer, DenyActivityAuthorizer>();
        services.TryAddSingleton<IWorkflowInvocationContextResolver, DenyWorkflowInvocationContextResolver>();
        services.TryAddScoped<IActivityCatalog, ActivityCatalog>();
        return services;
    }

    /// <summary>
    /// Explicit opt-in single-node WorkflowCore host rooted in an already verified home.
    /// Call only in a dedicated owner process; this does not modify gateway startup or register authored definitions.
    /// </summary>
    /// <param name="services">The dedicated owner's service collection.</param>
    /// <param name="home">The already verified absolute home containing the engine database.</param>
    /// <param name="fileSystem">Optional filesystem used for path checks and deferred directory creation.</param>
    /// <returns>The same collection, including the hosted engine lifecycle and transient catalog step.</returns>
    /// <exception cref="ArgumentNullException">The verified home is null.</exception>
    /// <exception cref="ArgumentException">The home path is empty or not absolute.</exception>
    /// <exception cref="InvalidOperationException">An embedded host is already registered in this collection.</exception>
    /// <remarks>
    /// WorkflowCore resolves each catalog step and its catalog, activities, and custom policies in an execution scope.
    /// Keep one owner per database. Catalog errors suspend execution; a crash after an external effect but before
    /// checkpoint persistence can still revisit a step, so effects require an idempotent attempt/reconciliation boundary.
    /// </remarks>
    public static IServiceCollection AddEmbeddedWorkflowCore(this IServiceCollection services, IVerifiedHome home, IFileSystem? fileSystem = null)
    {
        ArgumentNullException.ThrowIfNull(home);
        if (string.IsNullOrWhiteSpace(home.RootPath) || !Path.IsPathFullyQualified(home.RootPath))
            throw new ArgumentException("Workflow persistence requires an absolute verified home.", nameof(home));
        if (services.Any(descriptor => descriptor.ServiceType == typeof(EmbeddedWorkflowLifecycle)))
            throw new InvalidOperationException("Only one embedded workflow host may own a service collection.");
        fileSystem ??= new FileSystem();
        var databasePath = fileSystem.Path.Combine(home.RootPath, "workflows", "workflow-core.sqlite");
        HomeScope.EnsureWithin(home, databasePath);
        var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
        services.AddWorkflowActivities();
        services.AddTransient<CatalogActivityStep>();
        // Register the fixed engine store directly: this is not the platform configuration
        // backend selector. Keep the canonical configuration-authority ratchet unchanged.
        services.AddWorkflow(options => options.UsePersistence(_ =>
            new EntityFrameworkPersistenceProvider(new SqliteContextFactory(connectionString), true, false)));
        services.AddTransient(_ => new WorkflowPurger(new SqliteContextFactory(connectionString)));
        services.AddWorkflowStepMiddleware<CatalogActivitySafetyMiddleware>();
        services.AddSingleton(provider => new EmbeddedWorkflowLifecycle(provider.GetRequiredService<IWorkflowHost>(), databasePath, fileSystem));
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<EmbeddedWorkflowLifecycle>());
        return services;
    }
}

/// <summary>Owns engine start/stop without starting a gateway. Directory creation is deferred until host startup.</summary>
internal sealed class EmbeddedWorkflowLifecycle(IWorkflowHost host, string databasePath, IFileSystem fileSystem) : IHostedService
{
    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        fileSystem.Directory.CreateDirectory(fileSystem.Path.GetDirectoryName(databasePath) ?? throw new InvalidOperationException("Missing database directory."));
        await host.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => host.StopAsync(cancellationToken);
}
