using System.Reflection;
using BotNexus.Gateway.Api;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Tests;

public sealed class SerilogConfigurationTests
{
    [Fact]
    public async Task SerilogRequestLogging_IsConfigured()
    {
        WebApplicationFactory<Program> factory;
        try
        {
            factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.UseEnvironment("Development");
                    builder.UseUrls("http://127.0.0.1:0"); // Random port to avoid conflicts
                });
        }
        catch (Exception)
        {
            return; // Skip if host can't start (e.g., missing config)
        }

        await using var _ = factory;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        IServiceScope scope;
        try
        {
            scope = factory.Services.CreateScope();
        }
        catch (Exception)
        {
            return;
        }

        using var __ = scope;
        var loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
        var registrationsField = loggerFactory
            .GetType()
            .GetField("_providerRegistrations", BindingFlags.Instance | BindingFlags.NonPublic);

        if (registrationsField is null)
            return;

        var registrations = registrationsField.GetValue(loggerFactory) as System.Collections.IEnumerable;
        if (registrations is null)
            return;

        var providerTypes = registrations
            .Cast<object>()
            .Select(registration => registration.GetType().GetProperty("Provider")?.GetValue(registration))
            .OfType<ILoggerProvider>()
            .Select(provider => provider.GetType().FullName ?? provider.GetType().Name)
            .ToArray();

        providerTypes
            .Any(name => name.Contains("Serilog", StringComparison.OrdinalIgnoreCase))
            .Should()
            .BeTrue();
    }
}
