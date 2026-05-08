using BotNexus.Integration.Tests;

// Parse args
var filter = args.FirstOrDefault(a => !a.StartsWith("--"));
var scenarioDir = Path.Combine(AppContext.BaseDirectory, "scenarios");

Console.WriteLine("BotNexus Integration Test Harness");
Console.WriteLine("=================================");

var runner = new ScenarioRunner();
return await runner.RunAllAsync(scenarioDir, filter);