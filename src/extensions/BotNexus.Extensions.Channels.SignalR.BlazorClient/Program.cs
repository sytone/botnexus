using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using BotNexus.Extensions.Channels.SignalR.BlazorClient;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<GatewayHubConnection>();
builder.Services.AddScoped<AgentSessionManager>();

await builder.Build().RunAsync();
