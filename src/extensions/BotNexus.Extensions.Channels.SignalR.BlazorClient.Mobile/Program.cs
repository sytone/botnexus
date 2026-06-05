using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile.App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<GatewayHubConnection>();
builder.Services.AddScoped<IClientStateStore, ClientStateStore>();
builder.Services.AddScoped<IGatewayRestClient, GatewayRestClient>();
builder.Services.AddScoped<IChannelErrorReporter>(sp => (GatewayRestClient)sp.GetRequiredService<IGatewayRestClient>());
builder.Services.AddScoped<IGatewayEventHandler, GatewayEventHandler>();
builder.Services.AddScoped<IAgentInteractionService, AgentInteractionService>();
builder.Services.AddScoped<IPortalLoadService, PortalLoadService>();

await builder.Build().RunAsync();
