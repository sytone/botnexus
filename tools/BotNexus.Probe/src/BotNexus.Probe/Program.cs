using BotNexus.Probe;
using BotNexus.Probe.Api;
using BotNexus.Probe.Cli;
using BotNexus.Probe.Gateway;
using BotNexus.Probe.LogIngestion;
using BotNexus.Probe.Otel;

if (args.Length > 0 && CliRunner.IsCliCommand(args[0]))
{
    return await CliRunner.RunAsync(args);
}

var serveArgs = args.Length > 0 && args[0].Equals("serve", StringComparison.OrdinalIgnoreCase)
    ? args[1..]
    : args;

var options = ParseArgs(serveArgs);
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
    var sessionDbReader = TryCreateSessionDbReader(options.SessionDbPath);
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
    app.MapSessionsEndpoints(options, sessionReader, sessionDbReader);
    app.MapTraceEndpoints(traceStore, options.OtlpPort is not null);
    app.MapGatewayEndpoints(gatewayClient, gatewayHubClient);
    app.MapCorrelationEndpoints(options, logParser, sessionReader, sessionDbReader, traceStore, options.OtlpPort is not null);

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
    try
    {
        await app.RunAsync();
        return 0;
    }
    finally
    {
        sessionDbReader?.Dispose();
    }
}

static void PrintStartupBanner(ProbeOptions options)
{
    Console.WriteLine("BotNexus Probe started");
    Console.WriteLine($"  UI: http://localhost:{options.Port}");
    Console.WriteLine($"  Logs: {options.LogsPath}");
    Console.WriteLine($"  Sessions: {options.SessionsPath}");
    Console.WriteLine($"  Session DB: {options.SessionDbPath}");
    Console.WriteLine($"  Gateway: {(string.IsNullOrWhiteSpace(options.GatewayUrl) ? "disabled" : options.GatewayUrl)}");
    if (string.IsNullOrWhiteSpace(options.GatewayUrl))
    {
        Console.WriteLine("⚠️  Gateway not configured — live features disabled. Use --gateway <url> to connect.");
    }

    var otlpDisplay = options.OtlpPort is int p ? $"http://localhost:{p}/v1/traces" : "disabled";
    Console.WriteLine($"  OTLP Receiver: {otlpDisplay}");
}

static ProbeOptions ParseArgs(string[] args)
{
    var port = 5050;
    string? gateway = null;
    var logs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".botnexus", "logs");
    var sessions = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".botnexus", "sessions");
    var sessionDb = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".botnexus", "sessions.db");
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
            case "--session-db":
                sessionDb = nextValue ?? sessionDb;
                index++;
                break;
            case "--otlp-port" when int.TryParse(nextValue, out var parsedOtlpPort):
                otlpPort = parsedOtlpPort;
                index++;
                break;
        }
    }

    return new ProbeOptions(port, gateway, logs, sessions, sessionDb, otlpPort);
}

static SessionDbReader? TryCreateSessionDbReader(string sessionDbPath)
{
    if (!File.Exists(sessionDbPath))
    {
        return null;
    }

    try
    {
        return new SessionDbReader(sessionDbPath);
    }
    catch (Exception exception)
    {
        Console.WriteLine($"⚠️  SQLite session DB unavailable ({exception.Message}). Falling back to JSONL sessions.");
        return null;
    }
}
