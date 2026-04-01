using BotNexus.Core.Abstractions;
using BotNexus.Core.Configuration;
using BotNexus.Core.Extensions;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddBotNexusCore(builder.Configuration);
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

var app = builder.Build();

app.UseHttpsRedirection();
app.MapControllers();

// OpenAI-compatible health endpoint
app.MapGet("/v1/models", (IOptions<BotNexusConfig> config) =>
    new { data = new[] { new { id = config.Value.Agents.Model, @object = "model" } }, @object = "list" });

app.Run();
