using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace BotNexus.IntegrationTests;

public class ScenarioRunner
{
    public async Task<int> RunAllAsync(string scenarioDir, string? filter = null)
    {
        var logDir = Path.Combine(AppContext.BaseDirectory, "logs",
            $"run-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}");
        using var log = new TestLogger(logDir);

        var scenarios = LoadScenarios(scenarioDir, filter);
        log.Write($"Found {scenarios.Count} scenario(s)");
        Console.WriteLine($"Found {scenarios.Count} scenario(s)");

        // Use existing Release build if available, otherwise build
        var repoRoot = FindRepoRoot();
        var gatewayDll = Path.Combine(repoRoot, "src", "gateway", "BotNexus.Gateway.Api",
            "bin", "Release", "net10.0", "BotNexus.Gateway.Api.dll");

        if (!File.Exists(gatewayDll))
        {
            Console.Write("Building gateway... ");
            gatewayDll = await BuildGatewayAsync();
            Console.WriteLine("OK");
        }
        else
        {
            Console.WriteLine($"Using existing build: {gatewayDll}");
        }

        var passed = 0;
        var failed = 0;

        foreach (var scenario in scenarios)
        {
            log.WriteHeader(scenario.Name);

            try
            {
                await RunScenarioAsync(scenario, gatewayDll, log);
                log.WriteResult(scenario.Name, true);
                passed++;
            }
            catch (Exception ex)
            {
                log.WriteResult(scenario.Name, false, ex.Message);
                if (ex.InnerException is not null)
                    log.Write($"    Inner: {ex.InnerException.Message}");
                failed++;
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Results: {passed} passed, {failed} failed");
        log.Write($"Results: {passed} passed, {failed} failed");
        Console.WriteLine($"Logs: {logDir}");
        return failed > 0 ? 1 : 0;
    }

    private async Task RunScenarioAsync(ScenarioDefinition scenario, string gatewayDll, TestLogger log)
    {
        var port = GetFreePort();
        var baseUrl = $"http://127.0.0.1:{port}";
        var configDir = PrepareTestConfig();

        try
        {
            // Launch gateway process
            using var gateway = StartGateway(gatewayDll, port, configDir);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(scenario.TimeoutSeconds));

            // Wait for gateway to be ready
            await WaitForGatewayReady(baseUrl, cts.Token);
            log.Write("Gateway started on port " + port);

            // Register test agents
            using var httpClient = new HttpClient { BaseAddress = new Uri(baseUrl) };
            foreach (var agent in scenario.Agents)
            {
                await AgentRegistrar.RegisterAsync(httpClient, agent, cts.Token);
                log.Write($"Registered agent: {agent.Id}");
            }

            // Connect SignalR client
            await using var client = new TestSignalRClient(baseUrl, log);
            await client.ConnectAsync(cts.Token);
            await client.SubscribeAllAsync(cts.Token);

            // Execute steps
            var executor = new StepExecutor(client, httpClient, log);
            await executor.ExecuteStepsAsync(scenario.Steps, cts.Token);

            // Shut down gateway
            try { gateway.Kill(); } catch { }
        }
        finally
        {
            // Clean up test config directory
            try { Directory.Delete(configDir, true); } catch { }
        }
    }

    private static async Task<string> BuildGatewayAsync()
    {
        var repoRoot = FindRepoRoot();
        var projectPath = Path.Combine(repoRoot, "src", "gateway", "BotNexus.Gateway.Api",
            "BotNexus.Gateway.Api.csproj");

        var psi = new ProcessStartInfo("dotnet", $"build \"{projectPath}\" --nologo --tl:off -c Release")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = repoRoot
        };

        using var proc = Process.Start(psi) ?? throw new Exception("Failed to start dotnet build");
        await proc.WaitForExitAsync();

        if (proc.ExitCode != 0)
        {
            var stderr = await proc.StandardError.ReadToEndAsync();
            throw new Exception($"Gateway build failed (exit {proc.ExitCode}): {stderr}");
        }

        var dll = Path.Combine(repoRoot, "src", "gateway", "BotNexus.Gateway.Api",
            "bin", "Release", "net10.0", "BotNexus.Gateway.Api.dll");
        if (!File.Exists(dll))
            throw new FileNotFoundException($"Built gateway DLL not found: {dll}");

        return dll;
    }

    private static string PrepareTestConfig()
    {
        var testConfigDir = Path.Combine(Path.GetTempPath(), "botnexus-integration-test",
            Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(testConfigDir);

        var userHome = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".botnexus");

        // Copy auth.json (provider credentials)
        var authFile = Path.Combine(userHome, "auth.json");
        if (File.Exists(authFile))
            File.Copy(authFile, Path.Combine(testConfigDir, "auth.json"));

        // Copy the FULL config.json from the user's setup — we need providers, gateway settings, etc.
        // Only strip out agents (we register our own test agents via API)
        var userConfigPath = Path.Combine(userHome, "config.json");
        if (File.Exists(userConfigPath))
        {
            var userConfig = JsonDocument.Parse(File.ReadAllText(userConfigPath));
            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                foreach (var prop in userConfig.RootElement.EnumerateObject())
                {
                    if (prop.Name.Equals("agents", StringComparison.OrdinalIgnoreCase))
                    {
                        // Write empty agents — we register test agents via API
                        writer.WriteStartObject("agents");
                        writer.WriteEndObject();
                    }
                    else
                    {
                        prop.WriteTo(writer);
                    }
                }
                writer.WriteEndObject();
            }
            File.WriteAllBytes(Path.Combine(testConfigDir, "config.json"), ms.ToArray());
        }
        else
        {
            // Fallback: minimal config
            File.WriteAllText(Path.Combine(testConfigDir, "config.json"), "{}");
        }

        return testConfigDir;
    }

    private static Process StartGateway(string gatewayDll, int port, string configDir)
    {
        var psi = new ProcessStartInfo("dotnet", gatewayDll)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            Environment =
            {
                ["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}",
                ["ASPNETCORE_ENVIRONMENT"] = "Development",
                ["BOTNEXUS_HOME"] = configDir,
                ["BotNexus__ConfigPath"] = Path.Combine(configDir, "config.json")
            }
        };

        var proc = Process.Start(psi) ?? throw new Exception("Failed to start gateway process");
        return proc;
    }

    private static async Task WaitForGatewayReady(string baseUrl, CancellationToken ct)
    {
        using var http = new HttpClient();
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);

        while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            try
            {
                var response = await http.GetAsync($"{baseUrl}/api/agents", ct);
                if (response.IsSuccessStatusCode)
                    return;
            }
            catch (HttpRequestException) { }

            await Task.Delay(250, ct);
        }

        throw new TimeoutException($"Gateway did not become ready at {baseUrl} within 15 seconds");
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (dir.GetFiles("BotNexus.slnx").Length > 0)
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            $"Could not find BotNexus.slnx walking up from {AppContext.BaseDirectory}");
    }

    private static List<ScenarioDefinition> LoadScenarios(string dir, string? filter)
    {
        if (!Directory.Exists(dir))
            throw new DirectoryNotFoundException($"Scenario directory not found: {dir}");

        var files = Directory.GetFiles(dir, "*.json");
        var scenarios = new List<ScenarioDefinition>();

        foreach (var file in files.OrderBy(f => f))
        {
            if (filter is not null && !Path.GetFileNameWithoutExtension(file)
                .Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;

            var json = File.ReadAllText(file);
            var scenario = JsonSerializer.Deserialize<ScenarioDefinition>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidOperationException($"Failed to parse: {file}");

            scenarios.Add(scenario);
        }

        return scenarios;
    }
}