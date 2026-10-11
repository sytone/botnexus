using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using BotNexus.Integration.Testing;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BotNexus.Integration.Cli.Tests;

/// <summary>
/// Drives the packaged, out-of-process CLI provider-add command against a real isolated gateway,
/// then registers an agent through the gateway REST boundary using the newly-added provider.
/// A synthetic credential and non-routable endpoint prevent external LLM calls; this fixture
/// only checks registry activation and assignment, never a provider request.
/// </summary>
[Collection(LocalCliCollection.Name)]
public sealed class ProviderAddLiveGatewayAssignmentTests(LocalCliInstallFixture fixture)
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(2);

    [Fact]
    public async Task ProviderAdd_UpdatesRunningGatewayAndAllowsImmediateAgentAssignment()
    {
        fixture.Succeeded.ShouldBeTrue(
            $"Local pack/install fixture did not succeed. PackExitCode={fixture.PackExitCode}; " +
            $"InstallExitCode={fixture.InstallExitCode}; Error={fixture.Error}\n" +
            $"PackOutput:\n{fixture.PackOutput}\n\nInstallOutput:\n{fixture.InstallOutput}");

        var sandboxFamily = Path.Combine(Path.GetTempPath(), "botnexus-provider-live-assignment");
        SandboxProcessGuard.ReapStaleSandboxes(sandboxFamily);
        var sandbox = Path.Combine(sandboxFamily, Guid.NewGuid().ToString("N"));
        SandboxProcessGuard.MarkSandboxOwner(sandbox);
        var home = Path.Combine(sandbox, "home");
        Directory.CreateDirectory(home);
        try
        {
            var init = await RunCliAsync($"init --target \"{home}\"");
            init.ExitCode.ShouldBe(0, FormatCliFailure("init", init));

            var gateway = StartGateway(home);
            SandboxProcessGuard.KillOnExit(gateway.Process);
            SandboxProcessGuard.RecordSandboxGateway(sandbox, gateway.Process.Id);
            try
            {
                using var http = new HttpClient { BaseAddress = new Uri(gateway.BaseUrl), Timeout = TimeSpan.FromSeconds(2) };
                await WaitForGatewayAsync(http, gateway);

                const string providerName = "isolated-foundry-4195";
                const string modelId = "integration-model-4195";
                using var before = await http.GetAsync($"/api/providers/{providerName}/health");
                before.StatusCode.ShouldBe(HttpStatusCode.NotFound,
                    "the new instance must not exist in the running model registry before provider-add");

                var add = await RunCliAsync(
                    $"provider add --name {providerName} --api openai-responses --api-key integration-test-only " +
                    $"--base-url https://provider.example.invalid/v1 --default-model {modelId} " +
                    $"--model {modelId} --target \"{home}\"");
                add.ExitCode.ShouldBe(0, FormatCliFailure("provider add", add));

                await using var configStream = File.OpenRead(Path.Combine(home, "config.json"));
                using (var config = await JsonDocument.ParseAsync(configStream))
                {
                    var provider = config.RootElement.GetProperty("providers").GetProperty(providerName);
                    provider.GetProperty("enabled").GetBoolean().ShouldBeTrue();
                    provider.GetProperty("api").GetString().ShouldBe("openai-responses");
                    provider.GetProperty("apiKey").GetString().ShouldBe("integration-test-only");
                    provider.GetProperty("baseUrl").GetString().ShouldBe("https://provider.example.invalid/v1");
                    provider.GetProperty("defaultModel").GetString().ShouldBe(modelId);
                }

                using var activationDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                HttpResponseMessage? health = null;
                try
                {
                    await TestAwait.EventuallyAsync(
                        async () =>
                        {
                            health?.Dispose();
                            health = await http.GetAsync($"/api/providers/{providerName}/health", activationDeadline.Token);
                            if (!health.IsSuccessStatusCode)
                                return false;

                            using var responseJson = JsonDocument.Parse(
                                await health.Content.ReadAsStringAsync(activationDeadline.Token));
                            return responseJson.RootElement.GetProperty("status").GetString() == "healthy";
                        },
                        "running gateway to reload provider-add configuration and report a healthy provider",
                        timeout: TimeSpan.FromSeconds(20),
                        cancellationToken: activationDeadline.Token);

                    health.ShouldNotBeNull();
                    health.StatusCode.ShouldBe(HttpStatusCode.OK);
                    using var healthJson = JsonDocument.Parse(await health.Content.ReadAsStringAsync(activationDeadline.Token));
                    healthJson.RootElement.GetProperty("status").GetString().ShouldBe("healthy");
                    healthJson.RootElement.GetProperty("models").GetInt32().ShouldBeGreaterThan(0);
                    healthJson.RootElement.GetProperty("hasCredentials").GetBoolean().ShouldBeTrue();
                }
                finally
                {
                    health?.Dispose();
                }

                var agentId = $"provider-assignment-{Guid.NewGuid():N}";
                var descriptor = new
                {
                    agentId,
                    displayName = "Live provider assignment regression",
                    modelId,
                    apiProvider = providerName
                };
                using var assignment = await http.PostAsJsonAsync("/api/agents", descriptor);
                var responseBody = await assignment.Content.ReadAsStringAsync();
                assignment.StatusCode.ShouldBe(HttpStatusCode.Created,
                    $"live gateway rejected assignment to newly-activated provider: {(int)assignment.StatusCode} {responseBody}");

                using var assigned = JsonDocument.Parse(responseBody);
                assigned.RootElement.GetProperty("apiProvider").GetString().ShouldBe(providerName);
                assigned.RootElement.GetProperty("modelId").GetString().ShouldBe(modelId);
            }
            finally
            {
                await StopGatewayAsync(gateway);
            }
        }
        finally
        {
            try
            {
                if (Directory.Exists(sandbox))
                    Directory.Delete(sandbox, recursive: true);
            }
            catch
            {
                // Best-effort cleanup after the isolated gateway has stopped.
            }
        }
    }

    private static GatewayProcess StartGateway(string home)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var repo = RepoLocator.FindRepoRoot();
        var project = Path.Combine(repo, "src", "gateway", "BotNexus.Gateway.Api", "BotNexus.Gateway.Api.csproj");
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = repo,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("run");
        start.ArgumentList.Add("--project");
        start.ArgumentList.Add(project);
        start.ArgumentList.Add("--no-launch-profile");
        start.ArgumentList.Add("--");
        start.ArgumentList.Add("--urls");
        var baseUrl = $"http://127.0.0.1:{port}";
        start.ArgumentList.Add(baseUrl);
        var configPath = Path.Combine(home, "config.json");
        var config = JsonNode.Parse(File.ReadAllText(configPath))?.AsObject()
            ?? throw new InvalidDataException($"CLI init did not create a JSON object at {configPath}.");
        var gatewayConfig = config["gateway"]?.AsObject();
        if (gatewayConfig is null)
        {
            gatewayConfig = new JsonObject();
            config["gateway"] = gatewayConfig;
        }
        gatewayConfig["listenUrl"] = baseUrl;
        File.WriteAllText(configPath, config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        start.Environment["BOTNEXUS_HOME"] = home;
        start.Environment["BOTNEXUS_DATA_DIR"] = home;
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";

        var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start isolated gateway process.");
        var output = new ConcurrentQueue<string>();
        process.OutputDataReceived += (_, eventArgs) => { if (eventArgs.Data is not null) output.Enqueue(eventArgs.Data); };
        process.ErrorDataReceived += (_, eventArgs) => { if (eventArgs.Data is not null) output.Enqueue(eventArgs.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return new GatewayProcess(process, baseUrl, output);
    }

    private static Task WaitForGatewayAsync(HttpClient http, GatewayProcess gateway) => TestAwait.EventuallyAsync(
        async () =>
        {
            if (gateway.Process.HasExited)
                throw new InvalidOperationException(
                    $"Isolated gateway exited before readiness (exit {gateway.Process.ExitCode}).\n" +
                    string.Join(Environment.NewLine, gateway.Output));

            try
            {
                using var response = await http.GetAsync("/health");
                return response.IsSuccessStatusCode;
            }
            catch (HttpRequestException)
            {
                // Readiness probe only: the child process is expected to bind asynchronously.
                return false;
            }
            catch (TaskCanceledException)
            {
                // Per-request HTTP timeout; TestAwait's overall deadline remains authoritative.
                return false;
            }
        },
        "isolated gateway health endpoint",
        timeout: TimeSpan.FromMinutes(2));

    private Task<ProcessResult> RunCliAsync(string arguments) => ProcessRunner.RunAsync(
        fixture.CliExecutablePath,
        arguments,
        environment: new Dictionary<string, string?> { ["BOTNEXUS_HOME"] = null, ["BOTNEXUS_DATA_DIR"] = null },
        timeout: CommandTimeout);

    private static string FormatCliFailure(string command, ProcessResult result) =>
        $"Packaged CLI command '{command}' exited {result.ExitCode}.\nStdOut:\n{result.StdOut}\nStdErr:\n{result.StdErr}";

    private static async Task StopGatewayAsync(GatewayProcess gateway)
    {
        if (gateway.Process.HasExited)
        {
            await gateway.Process.WaitForExitAsync();
            gateway.Process.Dispose();
            return;
        }

        try
        {
            gateway.Process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) when (gateway.Process.HasExited)
        {
            // The process exited between the check and Kill.
        }

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await gateway.Process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("Isolated gateway process could not be stopped within ten seconds.");
        }
        finally
        {
            gateway.Process.Dispose();
        }
    }

    private sealed record GatewayProcess(Process Process, string BaseUrl, ConcurrentQueue<string> Output);
}
