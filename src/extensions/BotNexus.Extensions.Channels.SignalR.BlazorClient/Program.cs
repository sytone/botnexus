using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using BotNexus.Extensions.Channels.SignalR.BlazorClient;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<GatewayHubConnection>();
builder.Services.AddScoped<IClientStateStore, ClientStateStore>();
builder.Services.AddScoped<IGatewayRestClient, GatewayRestClient>();
builder.Services.AddScoped<IGatewayEventHandler, GatewayEventHandler>();
builder.Services.AddScoped<IAgentInteractionService, AgentInteractionService>();
builder.Services.AddScoped<IPortalLoadService, PortalLoadService>();
builder.Services.AddScoped<PlatformConfigService>();
builder.Services.AddScoped<GatewayInfoService>();
builder.Services.AddScoped<IUpdateStatusService, UpdateStatusService>();
builder.Services.AddScoped<LocationsApiClient>();
builder.Services.AddScoped<CronApiClient>();
builder.Services.AddScoped<IPortalPreferencesService, PortalPreferencesService>();

await builder.Build().RunAsync();
