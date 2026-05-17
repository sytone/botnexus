using BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile.App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Reuse service registrations from desktop client
builder.Services.AddBotNexusMobileServices(builder.Configuration);

await builder.Build().RunAsync();
