using BotNexus.Integration.Tests;

// Parse args
var filter = args.FirstOrDefault(a => !a.StartsWith("--"));

// --gateway-url=http://host:port targets an already-running gateway (e.g. a container).
// Falls back to BOTNEXUS_GATEWAY_URL env var. When neither is set the runner starts a
// local gateway process as before.
var gatewayUrlArg = args.FirstOrDefault(a => a.StartsWith("--gateway-url=", StringComparison.OrdinalIgnoreCase))
    ?.Split('=', 2)[1];
var gatewayUrl = gatewayUrlArg ?? Environment.GetEnvironmentVariable("BOTNEXUS_GATEWAY_URL");

// When --scenario-dir is set, use that directory; otherwise default to the embedded scenarios.
var scenarioDirArg = args.FirstOrDefault(a => a.StartsWith("--scenario-dir=", StringComparison.OrdinalIgnoreCase))
    ?.Split('=', 2)[1];
var scenarioDir = scenarioDirArg ?? Path.Combine(AppContext.BaseDirectory, "scenarios");

Console.WriteLine("BotNexus Integration Test Harness");
Console.WriteLine("=================================");
if (gatewayUrl is not null)
    Console.WriteLine($"Mode: container  gateway={gatewayUrl}");
else
    Console.WriteLine("Mode: local (spawns gateway process)");

var runner = new ScenarioRunner();
return await runner.RunAllAsync(scenarioDir, filter, gatewayUrl);