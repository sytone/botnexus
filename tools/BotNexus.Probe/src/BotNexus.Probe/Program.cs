using BotNexus.Probe;
using BotNexus.Probe.Api;
using BotNexus.Probe.Gateway;
using BotNexus.Probe.LogIngestion;
using BotNexus.Probe.Otel;

var options = ParseArgs(args);
return await RunHostAsync(options);

static async Task<int> RunHostAsync(ProbeOptions options)
{
    var builder = WebApplication.CreateBuilder();
    builder.WebHost.ConfigureKestrel(kestrel =>
    {
        kestrel.ListenAnyIP(options.Port);
        if (options.OtlpPort is int otlpPort)
        {
            kestrel.ListenAnyIP(otlpPort);
        }
    });

    var logParser = new SerilogFileParser();
    var sessionReader = new JsonlSessionReader();
    var traceStore = new TraceStore(10_000);
    var otlpReceiver = new OtlpTraceReceiver(traceStore);
    GatewayClient? gatewayClient = string.IsNullOrWhiteSpace(options.GatewayUrl) ? null : new GatewayClient(options.GatewayUrl);
    GatewayHubClient? gatewayHubClient = string.IsNullOrWhiteSpace(options.GatewayUrl) ? null : new GatewayHubClient(options.GatewayUrl);

    var app = builder.Build();

    if (Directory.Exists(Path.Combine(app.Environment.ContentRootPath, "wwwroot")))
    {
        app.UseDefaultFiles();
        app.UseStaticFiles();
    }

    app.MapLogsEndpoints(options, logParser);
    app.MapSessionsEndpoints(options, sessionReader);
    app.MapTraceEndpoints(traceStore, options.OtlpPort is not null);
    app.MapGatewayEndpoints(gatewayClient, gatewayHubClient);
    app.MapCorrelationEndpoints(options, logParser, sessionReader, traceStore, options.OtlpPort is not null);

    if (options.OtlpPort is not null)
    {
        otlpReceiver.MapEndpoint(app);
    }

    app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

    if (gatewayHubClient is not null)
    {
        app.Lifetime.ApplicationStarted.Register(() => _ = Task.Run(() => gatewayHubClient.StartAsync(app.Lifetime.ApplicationStopping)));
        app.Lifetime.ApplicationStopping.Register(() => _ = Task.Run(() => gatewayHubClient.StopAsync(CancellationToken.None)));
    }

    PrintStartupBanner(options);
    await app.RunAsync();
    return 0;
}

static void PrintStartupBanner(ProbeOptions options)
{
    Console.WriteLine("BotNexus Probe started");
    Console.WriteLine($"  UI: http://localhost:{options.Port}");
    Console.WriteLine($"  Logs: {options.LogsPath}");
    Console.WriteLine($"  Sessions: {options.SessionsPath}");
    Console.WriteLine($"  Gateway: {(string.IsNullOrWhiteSpace(options.GatewayUrl) ? "disabled" : options.GatewayUrl)}");
    var otlpDisplay = options.OtlpPort is int p ? $"http://localhost:{p}/v1/traces" : "disabled";
    Console.WriteLine($"  OTLP Receiver: {otlpDisplay}");
}

static ProbeOptions ParseArgs(string[] args)
{
    var port = 5050;
    string? gateway = null;
    var logs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".botnexus", "logs");
    var sessions = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".botnexus", "sessions");
    int? otlpPort = null;

    for (var index = 0; index < args.Length; index++)
    {
        var arg = args[index];
        if (!arg.StartsWith("--", StringComparison.Ordinal))
        {
            continue;
        }

        var nextValue = index + 1 < args.Length ? args[index + 1] : null;
        switch (arg)
        {
            case "--port" when int.TryParse(nextValue, out var parsedPort):
                port = parsedPort;
                index++;
                break;
            case "--gateway":
                gateway = nextValue;
                index++;
                break;
            case "--logs":
                logs = nextValue ?? logs;
                index++;
                break;
            case "--sessions":
                sessions = nextValue ?? sessions;
                index++;
                break;
            case "--otlp-port" when int.TryParse(nextValue, out var parsedOtlpPort):
                otlpPort = parsedOtlpPort;
                index++;
                break;
        }
    }

    return new ProbeOptions(port, gateway, logs, sessions, otlpPort);
}
