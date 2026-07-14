using BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile.Services;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
// #1951: the mobile chat palette consumes the SAME shared Core slash-command dispatcher the
// desktop ChatPanel uses (registry + dispatcher from #1949, approval hook from #1950), giving
// full command parity across clients. The approval hook is optional; when no implementation is
// registered the dispatcher fails closed for protected commands.
builder.Services.AddScoped<BotNexus.Extensions.Channels.SignalR.BlazorClient.Services.SlashCommands.ISlashCommandDispatcher, BotNexus.Extensions.Channels.SignalR.BlazorClient.Services.SlashCommands.SlashCommandDispatcher>();
builder.Services.AddScoped<IPortalLoadService, PortalLoadService>();
// #1615: the schema-driven mobile Settings page reads/writes platform config through this service
// (GET /api/config/schema + PUT /api/config/{section}) -- the same client service the desktop uses.
builder.Services.AddScoped<PlatformConfigService>();

// #1893: dynamic option sources for schema-driven config select widgets (provider model dropdowns).
// Same abstraction the desktop registers; SchemaForm lives in Core and depends on it, so mobile
// must provide an implementation too.
builder.Services.AddScoped<IModelOptionsProvider, HttpModelOptionsProvider>();

// #1840: bind mobile-scoped SignalR keep-alive/timeout tuning from appsettings (section "SignalR")
// with mobile defaults, so a tunnelled/backgrounded PWA gets a longer server timeout and a
// tunnel-friendly keep-alive cadence. Registered as a singleton so the Chat page can apply it to
// the hub connection. Mobile-only: the desktop client never constructs this.
var mobileTuning = new MobileHubTuningOptions();
builder.Configuration.GetSection("SignalR").Bind(mobileTuning);
builder.Services.AddSingleton(mobileTuning);

await builder.Build().RunAsync();
