using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using BotNexus.Extensions.Channels.SignalR.BlazorClient;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services.Abstractions;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<GatewayHubConnection>();
builder.Services.AddScoped<IClientStateStore, ClientStateStore>();
builder.Services.AddScoped<IGatewayRestClient, GatewayRestClient>();
builder.Services.AddScoped<IChannelErrorReporter>(sp => (GatewayRestClient)sp.GetRequiredService<IGatewayRestClient>());
builder.Services.AddScoped<IGatewayEventHandler, GatewayEventHandler>();
builder.Services.AddScoped<IAgentInteractionService, AgentInteractionService>();
builder.Services.AddScoped<BotNexus.Extensions.Channels.SignalR.BlazorClient.Services.SlashCommands.ISlashCommandDispatcher, BotNexus.Extensions.Channels.SignalR.BlazorClient.Services.SlashCommands.SlashCommandDispatcher>();
builder.Services.AddScoped<IPortalLoadService, PortalLoadService>();
builder.Services.AddScoped<PlatformConfigService>();
// #1893: dynamic option sources for config select widgets (e.g. provider model dropdowns).
builder.Services.AddScoped<IModelOptionsProvider, HttpModelOptionsProvider>();
builder.Services.AddScoped<GatewayInfoService>();
builder.Services.AddScoped<ExtensionFeatureService>();
builder.Services.AddScoped<IUpdateStatusService, UpdateStatusService>();
builder.Services.AddScoped<LocationsApiClient>();
builder.Services.AddScoped<CronApiClient>();
builder.Services.AddScoped<SectionsApiClient>();
builder.Services.AddScoped<IPortalPreferencesService, PortalPreferencesService>();

await builder.Build().RunAsync();
