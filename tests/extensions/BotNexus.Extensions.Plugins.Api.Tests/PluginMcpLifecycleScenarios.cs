using System.IO.Abstractions;
using System.Text.Json;
using System.Net;
using System.Reflection;
using BotNexus.Extensions.Mcp.Transport;
using BotNexus.Extensions.Mcp.Protocol;
using BotNexus.Agent.Core.Tools;
using BotNexus.Extensions.Mcp;
using BotNexus.Extensions.Mcp.Plugins;
using BotNexus.Extensions.Plugins.Lifecycle;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Extensions.Plugins.Api.Tests;

// Shared by xUnit and a standalone, no-host console runner. Only the fetch and server boundaries
// are fakes: production DI, lifecycle, registrar, declaration reader and disk store are exercised.
public static class PluginMcpLifecycleScenarios
{
    public static async Task RunAllAsync()
    {
        var failures = new List<string>();
        foreach (var (name, run) in new (string, Func<Task>)[]
        {
            ("candidate collision preserves unowned file before activation failure", () => CandidateCollision("local.txt", "local.txt", false)),
            ("portable directory case collision is rejected", () => CandidateCollision("CUSTOM/local.txt", "custom/servers.json", false)),
            ("portable case collision is rejected", () => CandidateCollision("LOCAL.txt", "local.txt", false)),
            ("file blocks candidate directory before promotion", () => CandidateCollision("custom", "custom/servers.json", false)),
            ("directory blocks candidate file", () => CandidateCollision("local.txt/keep.txt", "local.txt", false)),
            ("unowned trust catalog is rejected", () => CandidateCollision("trust.json", "trust.json", true)),
            ("backup failure leaves source + runtime untouched", BackupFailure),
            ("lifecycle diagnostics never expose credentials", SafeDiagnostics),
            ("strict manager transport failure retains retry ownership", StrictTransportRetry),
            ("composed removal keeps ownership after real stdio stop failure", ComposedStdioStopFailure),
            ("strict HTTP delete failure retains retry session", HttpDeleteRetry),
            ("strict HTTP protocol-valid statuses succeed", HttpDeleteStatuses),
            ("strict HTTP network failure retries", HttpNetworkRetry),
            ("strict stdio exit wait failure retries", StdioExitWaitRetry),
            ("strict stdio graceful exit succeeds", StdioGracefulExit),
            ("strict stdio termination failure + retry", StdioTerminationRetry),
            ("declared install + scoped removal", DeclaredInstallAndRemove),
            ("null convention + no declaration", ConventionAndEmpty),
            ("Enforce denies before startup", EnforceDenies),
            ("partial startup compensation", PartialStartup),
            ("cancelled startup compensation", CancelledStartup),
            ("failed startup compensation retains receipt", FailedInstallCompensation),
            ("failed teardown preserves files + retry ownership", FailedTeardown),
            ("update replaces server set", UpdateReplaces),
            ("failed update restores last known good", FailedUpdate),
            ("cancelled update restores last known good", CancelledUpdate),
            ("failed update compensation is explicit", FailedUpdateCompensation),
            ("registrar retains ownership after failed teardown", RegistrarFailure),
            ("update teardown failure retains old content", UpdateTeardownFailure),
            ("strict existing manager rejects absent transport", StrictManagerRejectsMissingTransport),
            ("missing executable releases production manager ownership", MissingExecutableManager),
            ("missing executable install compensates through real manager", MissingExecutableInstall),
            ("install + removal await async runtime boundary", AwaitedBoundaries),
        })
        {
            try { await run(); Console.WriteLine($"PASS {name}"); }
            catch (Exception ex) { failures.Add(name + ": " + ex.Message); Console.WriteLine($"FAIL {name}: {ex.Message}"); }
        }
        if (failures.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
        Console.WriteLine("All 33 lifecycle safety scenarios passed (no gateway/test host, child process, or network).");
    }

    private const string SensitiveDiagnostic = "https://user:credential-secret@example.test/mcp?token=secret-value";

    private static async Task CandidateCollision(string local, string candidate, bool unownCatalog)
    {
        await using var f = new Fixture();
        f.Fetch.Enqueue("alpha", "v1", "old");
        await f.Manager.InstallAsync(new() { Source = "fake" });
        if (unownCatalog)
        {
            var receipt = f.Store.Find("alpha") ?? throw new InvalidOperationException("missing receipt");
            f.Store.Upsert(receipt with { Files = receipt.Files.Where(x => x != "trust.json").ToArray() });
        }
        var localPath = Path.Combine(f.PluginDir, local);
        Directory.CreateDirectory(Path.GetDirectoryName(localPath) ?? throw new InvalidOperationException("no parent"));
        File.WriteAllText(localPath, "user-owned-original");
        var before = JsonSerializer.Serialize(f.Store.Find("alpha"));
        f.Host.FailStart = 2;
        f.Fetch.ExtraFile = candidate;
        f.Fetch.Enqueue("alpha", "v2", "new", explicitPath: true);
        var result = await f.Manager.UpdateAsync("alpha");
        Check(!result.IsSuccess, "collision reported success");
        Check(File.Exists(localPath) && File.ReadAllText(localPath) == "user-owned-original", "collision overwrote/deleted unowned bytes");
        Check(JsonSerializer.Serialize(f.Store.Find("alpha")) == before, "collision changed old receipt");
        Check(f.Host.Stops == 0 && f.Host.Starts == 1 && f.Host.Running.SetEquals(["plugin:alpha:old"]), "collision retired/restarted old server");
    }

    private static async Task BackupFailure()
    {
        await using var f = new Fixture();
        f.Fetch.Enqueue("alpha", "v1", "old");
        await f.Manager.InstallAsync(new() { Source = "fake" });
        var receipt = f.Store.Find("alpha") ?? throw new InvalidOperationException("missing receipt");
        // A deterministic invalid backup target after at least one copy; no OS-specific locks.
        f.Store.Upsert(receipt with { Files = receipt.Files.Concat(["payload.txt/child"]).ToArray() });
        // Missing recorded content must refuse the update, not silently make an incomplete backup.
        f.Fetch.Enqueue("alpha", "v2", "new");
        var before = JsonSerializer.Serialize(f.Store.Find("alpha"));
        var result = await f.Manager.UpdateAsync("alpha");
        Check(!result.IsSuccess, "incomplete backup allowed update");
        Check(f.Host.Stops == 0 && f.Host.Starts == 1, "backup failure mutated runtime");
        Check(File.ReadAllText(Path.Combine(f.PluginDir, "payload.txt")) == "v1", "backup failure mutated source");
        Check(JsonSerializer.Serialize(f.Store.Find("alpha")) == before, "backup failure changed receipt");
    }

    private static async Task SafeDiagnostics()
    {
        await using var f = new Fixture();
        f.Host.FailStart = 1;
        f.Fetch.Enqueue("alpha", "v1", "old");
        var install = await f.Manager.InstallAsync(new() { Source = "fake" });
        AssertSafe(install);
        f.Host.FailStart = 0;
        f.Fetch.Enqueue("alpha", "v1", "old");
        await f.Manager.InstallAsync(new() { Source = "fake" });
        f.Host.FailStop = true;
        AssertSafe(await f.Manager.RemoveAsync("alpha"));
        f.Host.FailStop = false;
        f.Host.FailStart = f.Host.Starts + 1;
        f.Fetch.Enqueue("alpha", "v2", "new");
        AssertSafe(await f.Manager.UpdateAsync("alpha"));
        f.Fetch.FailFetch = true;
        AssertSafe(await f.Manager.InstallAsync(new() { Source = SensitiveDiagnostic }));
        static void AssertSafe(PluginOperationResult result)
        {
            Check(!result.IsSuccess, "diagnostic scenario unexpectedly succeeded");
            var text = string.Join(" ", result.Errors.Select(x => x.Message));
            Check(!text.Contains("credential-secret", StringComparison.Ordinal) && !text.Contains("secret-value", StringComparison.Ordinal), "public lifecycle error leaked credentials");
        }
    }

    private static Task StopStrict(McpClient client) => client.StopAsync();

    private static async Task ComposedStdioStopFailure()
    {
        await using var manager = new McpServerManager();
        await using var f = new Fixture();
        f.Fetch.Enqueue("alpha", "v1", "old");
        await f.Manager.InstallAsync(new() { Source = "fake" });
        var transport = new StdioMcpTransport("never-executed");
        var property = typeof(StdioMcpTransport).GetProperty("TerminationBoundary", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing process boundary");
        var exited = false;
        var failKill = true;
        property.SetValue(transport, ((Func<bool>)(() => exited), (Func<CancellationToken, Task>)(_ => exited ? Task.CompletedTask : Task.FromException(new OperationCanceledException())), (Action)(() => { if (failKill) throw new IOException(SensitiveDiagnostic); exited = true; })));
        var clients = typeof(McpServerManager).GetField("_clients", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(manager) as List<McpClient>
            ?? throw new InvalidOperationException("manager client seam missing");
        clients.Add(new(transport, "plugin:alpha:old"));
        f.Host.StopBoundary = () => manager.StopServersAsync(_ => true);
        var result = await f.Manager.RemoveAsync("alpha");
        Check(!result.IsSuccess, "unconfirmed process stop reported Removed");
        Check(f.Store.Find("alpha") is not null && File.ReadAllText(Path.Combine(f.PluginDir, "payload.txt")) == "v1", "failed process stop removed content/receipt");
        Check(f.Registrar.GetRegisteredServerNames("alpha").Count == 1 && manager.GetClients().Count == 1, "failed process stop lost retry ownership");
        Check(!result.Errors.Single().Message.Contains("credential-secret", StringComparison.Ordinal), "process stop error leaked credentials");
        failKill = false;
        Check((await f.Manager.RemoveAsync("alpha")).IsSuccess && exited && manager.GetClients().Count == 0, "composed process stop retry did not recover");
    }

    private static async Task HttpNetworkRetry()
    {
        using var handler = new DeleteHandler { ThrowDelete = true };
        using var http = new HttpClient(handler);
        var transport = new HttpSseMcpTransport(new Uri("https://never-contacted.test/mcp"), httpClient: http);
        await transport.ConnectAsync();
        await transport.SendAsync(new JsonRpcRequest { Id = 1, Method = "initialize" });
        var client = new McpClient(transport, "plugin:alpha:http");
        var threw = false;
        try { await client.StopAsync(); } catch (HttpRequestException) { threw = true; }
        Check(threw, "network failure was swallowed");
        handler.ThrowDelete = false;
        handler.Status = HttpStatusCode.NoContent;
        await client.StopAsync();
        Check(handler.Deletes == 2, "network failure lost retry session");
    }

    private static async Task StdioExitWaitRetry()
    {
        var transport = new StdioMcpTransport("never-executed");
        var property = typeof(StdioMcpTransport).GetProperty("TerminationBoundary", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing process boundary");
        var exited = false;
        var kills = 0;
        property.SetValue(transport, ((Func<bool>)(() => exited), (Func<CancellationToken, Task>)(_ => Task.FromException(new OperationCanceledException())), (Action)(() => kills++)));
        var client = new McpClient(transport, "plugin:alpha:stdio");
        var threw = false;
        try { await client.StopAsync(); } catch (OperationCanceledException) { threw = true; }
        Check(threw && kills == 1, "unconfirmed exit was accepted");
        exited = true;
        await client.StopAsync();
        await client.DisposeAsync();
        Check(kills == 1, "retry killed an already-exited process");
    }

    private static async Task StdioGracefulExit()
    {
        var transport = new StdioMcpTransport("never-executed");
        var property = typeof(StdioMcpTransport).GetProperty("TerminationBoundary", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing process boundary");
        var exited = false;
        property.SetValue(transport, ((Func<bool>)(() => exited), (Func<CancellationToken, Task>)(_ => { exited = true; return Task.CompletedTask; }), (Action)(() => throw new IOException("graceful exit must not kill"))));
        await new McpClient(transport, "plugin:alpha:stdio").StopAsync();
        Check(exited, "graceful termination was not confirmed");
    }

    private static async Task StrictTransportRetry()
    {
        await using var manager = new McpServerManager();
        var failed = new RetryTransport { FailDisconnect = true };
        var partial = new RetryTransport { FailDispose = true };
        var good = new RetryTransport();
        var clients = typeof(McpServerManager).GetField("_clients", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(manager) as List<McpClient>
            ?? throw new InvalidOperationException("manager client seam missing");
        clients.AddRange([new(good, "plugin:alpha:good"), new(failed, "plugin:alpha:failed"), new(partial, "plugin:alpha:partial")]);
        try { await manager.StopServersAsync(_ => true); }
        catch (AggregateException) { }
        Check(manager.GetClients().Count == 2, "strict transport failure lost retry ownership");
        Check(good.Disposals == 1 && failed.Disposals == 0, "strict failure prematurely disposed transport");
        failed.FailDisconnect = false;
        partial.FailDispose = false;
        await manager.StopServersAsync(_ => true);
        Check(manager.GetClients().Count == 0 && partial.Disposals == 2, "partial cleanup retry failed");
    }

    private static async Task HttpDeleteRetry()
    {
        using var handler = new DeleteHandler { Status = HttpStatusCode.InternalServerError };
        using var http = new HttpClient(handler);
        var transport = new HttpSseMcpTransport(new Uri("https://never-contacted.test/mcp"), httpClient: http);
        await transport.ConnectAsync();
        await transport.SendAsync(new JsonRpcRequest { Id = 1, Method = "initialize" });
        var client = new McpClient(transport, "plugin:alpha:http");
        var threw = false;
        try { await StopStrict(client); } catch (HttpRequestException) { threw = true; }
        Check(threw, "strict HTTP teardown swallowed DELETE failure");
        handler.Status = HttpStatusCode.NoContent;
        await StopStrict(client);
        await StopStrict(client);
        Check(handler.Deletes == 2 && handler.DeleteSessions.All(x => x == "session-3347"), "HTTP teardown lost session retry or repeated completed disposal");
    }

    private static async Task HttpDeleteStatuses()
    {
        foreach (var status in new[] { HttpStatusCode.NoContent, HttpStatusCode.MethodNotAllowed, HttpStatusCode.NotFound })
        {
            using var handler = new DeleteHandler { Status = status };
            using var http = new HttpClient(handler);
            var transport = new HttpSseMcpTransport(new Uri("https://never-contacted.test/mcp"), httpClient: http);
            await transport.ConnectAsync();
            await transport.SendAsync(new JsonRpcRequest { Id = 1, Method = "initialize" });
            var client = new McpClient(transport, "plugin:alpha:http");
            await StopStrict(client);
            await StopStrict(client);
            Check(handler.Deletes == 1, "completed HTTP stop was not idempotent");
        }
    }

    private static async Task StdioTerminationRetry()
    {
        var transport = new StdioMcpTransport("never-executed");
        var property = typeof(StdioMcpTransport).GetProperty("TerminationBoundary", BindingFlags.Instance | BindingFlags.NonPublic);
        Check(property is not null, "stdio termination has no injectable process boundary");
        var exited = false;
        var killFails = true;
        var kills = 0;
        Func<bool> hasExited = () => exited;
        Func<CancellationToken, Task> wait = _ => exited ? Task.CompletedTask : Task.FromException(new OperationCanceledException());
        Action kill = () => { kills++; if (killFails) throw new IOException("kill denied"); exited = true; };
        property?.SetValue(transport, (hasExited, wait, kill));
        var client = new McpClient(transport, "plugin:alpha:stdio");
        var threw = false;
        try { await StopStrict(client); } catch (IOException) { threw = true; }
        Check(threw && !exited, "strict stdio stop swallowed kill/exit failure");
        killFails = false;
        await StopStrict(client);
        await StopStrict(client);
        Check(exited && kills == 2, "stdio retry failed or re-killed a stopped process");
        // Already-exited process must not be a false failure, even if Kill would throw.
        var stopped = new StdioMcpTransport("never-executed");
        property?.SetValue(stopped, ((Func<bool>)(() => true), wait, (Action)(() => throw new IOException("must not kill"))));
        await StopStrict(new McpClient(stopped, "plugin:alpha:stopped"));
    }

    private sealed class RetryTransport : IMcpTransport
    {
        public bool FailDisconnect { get; set; }
        public bool FailDispose { get; set; }
        public int Disposals { get; private set; }
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SendAsync(JsonRpcRequest message, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendNotificationAsync(JsonRpcNotification message, CancellationToken ct = default) => Task.CompletedTask;
        public Task<JsonRpcResponse> ReceiveAsync(CancellationToken ct = default) => Task.FromResult(new JsonRpcResponse());
        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask; // best effort masks errors
        public Task DisconnectStrictAsync(CancellationToken ct = default) => FailDisconnect ? Task.FromException(new IOException("stop denied")) : Task.CompletedTask;
        public ValueTask DisposeAsync()
        {
            Disposals++;
            return FailDispose ? ValueTask.FromException(new IOException("partial cleanup")) : ValueTask.CompletedTask;
        }
    }

    private sealed class DeleteHandler : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; }
        public int Deletes { get; private set; }
        public bool ThrowDelete { get; set; }
        public List<string> DeleteSessions { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.MethodNotAllowed));
            if (request.Method == HttpMethod.Delete)
            {
                Deletes++;
                DeleteSessions.Add(request.Headers.GetValues("Mcp-Session-Id").Single());
                if (ThrowDelete) throw new HttpRequestException("network down");
                return Task.FromResult(new HttpResponseMessage(Status));
            }
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}", System.Text.Encoding.UTF8, "application/json") };
            response.Headers.Add("Mcp-Session-Id", "session-3347");
            return Task.FromResult(response);
        }
    }

    private static async Task DeclaredInstallAndRemove()
    {
        await using var f = new Fixture();
        f.Fetch.Enqueue("alpha", "v1", "one", explicitPath: true);
        Check((await f.Manager.InstallAsync(new() { Source = "fake" })).IsSuccess, "install failed");
        Check(f.Host.Running.SetEquals(["plugin:alpha:one"]), "declared server did not start");
        f.Fetch.Enqueue("beta", "v1", "one", explicitPath: true);
        await f.Manager.InstallAsync(new() { Source = "fake" });
        f.Host.Running.Add("user-configured");
        Check(f.Manager.Remove("alpha").IsSuccess, "sync removal failed");
        Check(f.Host.Running.SetEquals(["plugin:beta:one", "user-configured"]), "removal crossed ownership or leaked alpha");
        Check(f.Store.Find("alpha") is null, "removed receipt remains");
    }

    private static async Task ConventionAndEmpty()
    {
        await using var f = new Fixture();
        f.Fetch.Enqueue("alpha", "v1", "one");
        await f.Manager.InstallAsync(new() { Source = "fake" });
        Check(f.Host.Running.Contains("plugin:alpha:one"), "null mcpServers lost convention discovery");
        f.Fetch.Enqueue("beta", "v1", null);
        Check((await f.Manager.InstallAsync(new() { Source = "fake" })).IsSuccess, "no-server plugin failed");
        Check(f.Host.Starts == 1, "empty plugin started a server");
    }

    private static async Task EnforceDenies()
    {
        await using var f = new Fixture(deny: true);
        f.Fetch.Enqueue("alpha", "v1", "one");
        Check(!(await f.Manager.InstallAsync(new() { Source = "fake" })).IsSuccess, "denied activation reported install success");
        Check(f.Host.Starts == 0, "Enforce started a server before denial");
        Check(f.Store.Find("alpha") is null, "denied install not rolled back");
    }

    private static async Task PartialStartup()
    {
        await using var f = new Fixture();
        f.Host.FailStart = 2;
        f.Fetch.Enqueue("alpha", "v1", "one,two");
        Check(!(await f.Manager.InstallAsync(new() { Source = "fake" })).IsSuccess, "partial activation reported success");
        Check(f.Host.Starts == 2 && f.Host.Running.Count == 0, "partial startup leaked servers");
        Check(f.Store.Find("alpha") is null && !Directory.Exists(f.PluginDir), "failed install left content/receipt");
    }

    private static async Task CancelledStartup()
    {
        await using var f = new Fixture();
        f.Host.CancelStart = 2;
        f.Fetch.Enqueue("alpha", "v1", "one,two");
        await ExpectCancellation(() => f.Manager.InstallAsync(new() { Source = "fake" }));
        Check(f.Host.Running.Count == 0 && f.Store.Find("alpha") is null, "cancelled install not compensated");
    }

    private static async Task FailedInstallCompensation()
    {
        await using var f = new Fixture();
        f.Host.FailStart = 2;
        f.Host.FailStop = true;
        f.Fetch.Enqueue("alpha", "v1", "one,two");
        var result = await f.Manager.InstallAsync(new() { Source = "fake" });
        Check(!result.IsSuccess && result.Errors.Any(e => e.Message.Contains("compensation", StringComparison.OrdinalIgnoreCase)), "failed compensation not explicit");
        Check(f.Store.Find("alpha") is not null && Directory.Exists(f.PluginDir), "lost recovery receipt while server might run");
        f.Host.FailStop = false;
        Check(f.Manager.Remove("alpha").IsSuccess && f.Host.Running.Count == 0, "retained receipt cannot recover leaked server");
    }

    private static async Task FailedTeardown()
    {
        await using var f = new Fixture();
        f.Fetch.Enqueue("alpha", "v1", "one");
        await f.Manager.InstallAsync(new() { Source = "fake" });
        f.Host.FailStop = true;
        Check(!f.Manager.Remove("alpha").IsSuccess, "failed teardown reported Removed");
        Check(f.Store.Find("alpha") is not null && File.Exists(Path.Combine(f.PluginDir, "payload.txt")), "teardown deleted content/receipt too early");
        Check(f.Registrar.GetRegisteredServerNames("alpha").Count == 1, "teardown lost retry ownership");
        f.Host.FailStop = false;
        Check(f.Manager.Remove("alpha").IsSuccess && f.Host.Running.Count == 0, "teardown retry did not recover");
    }

    private static async Task UpdateReplaces()
    {
        await using var f = new Fixture();
        f.Fetch.Enqueue("alpha", "v1", "old");
        await f.Manager.InstallAsync(new() { Source = "fake" });
        File.WriteAllText(Path.Combine(f.PluginDir, "local.txt"), "keep");
        f.Fetch.Enqueue("alpha", "v2", "new", explicitPath: true);
        Check((await f.Manager.UpdateAsync("alpha")).IsSuccess, "update failed");
        Check(f.Host.Running.SetEquals(["plugin:alpha:new"]), "old server survived update or new not started");
        Check(File.ReadAllText(Path.Combine(f.PluginDir, "local.txt")) == "keep", "update deleted unowned file");
    }

    private static async Task FailedUpdate() => await UpdateFailure(cancel: false, failCompensation: false);
    private static async Task CancelledUpdate() => await UpdateFailure(cancel: true, failCompensation: false);
    private static async Task FailedUpdateCompensation() => await UpdateFailure(cancel: false, failCompensation: true);

    private static async Task UpdateFailure(bool cancel, bool failCompensation)
    {
        await using var f = new Fixture();
        f.Fetch.Enqueue("alpha", "v1", "old");
        await f.Manager.InstallAsync(new() { Source = "fake" });
        File.WriteAllText(Path.Combine(f.PluginDir, "local.txt"), "keep");
        f.Host.FailStart = cancel ? 0 : 2;
        f.Host.CancelStart = cancel ? 2 : 0;
        f.Host.FailAfterStart = failCompensation ? 3 : 0;
        f.Fetch.Enqueue("alpha", "v2", "new", explicitPath: true);
        if (cancel) await ExpectCancellation(() => f.Manager.UpdateAsync("alpha"));
        else
        {
            var result = await f.Manager.UpdateAsync("alpha");
            Check(!result.IsSuccess, "failed activation reported Updated");
            if (failCompensation)
            {
                Check(result.Errors.Any(e => e.Message.Contains("compensation", StringComparison.OrdinalIgnoreCase)), "compensation failure hidden");
                var message = result.Errors.Single().Message;
                Check(!message.Contains(f.StagingRoot, StringComparison.Ordinal) && !message.Contains("credential-secret", StringComparison.Ordinal), "public compensation error exposed private details");
                var snapshot = Directory.EnumerateDirectories(f.StagingRoot).Single();
                Check(File.Exists(Path.Combine(snapshot, "payload.txt")), "failed compensation lost recovery snapshot");
                Directory.Delete(snapshot, recursive: true);
            }
        }
        Check(f.Store.Find("alpha")?.ResolvedVersion == "v1", "old receipt not restored");
        Check(File.ReadAllText(Path.Combine(f.PluginDir, "payload.txt")) == "v1", "old content not restored");
        Check(File.ReadAllText(Path.Combine(f.PluginDir, "local.txt")) == "keep", "compensation deleted unowned file");
        if (!failCompensation) Check(f.Host.Running.SetEquals(["plugin:alpha:old"]), "last known good servers not restored");
        Check(!File.Exists(Path.Combine(f.PluginDir, "custom", "servers.json")), "candidate-only file survived rollback");
    }

    private static async Task RegistrarFailure()
    {
        await using var f = new Fixture();
        f.Fetch.Enqueue("alpha", "v1", "one");
        await f.Manager.InstallAsync(new() { Source = "fake" });
        // Direct exercise also catches ownership loss independently of lifecycle result conversion.
        if (f.Host.Starts == 0) await f.Registrar.RegisterAsync("alpha", f.PluginDir);
        f.Host.FailStop = true;
        try { await f.Registrar.UnregisterAsync("alpha"); throw new InvalidOperationException("stop failure swallowed"); }
        catch (IOException) { }
        Check(f.Registrar.GetRegisteredServerNames("alpha").Count == 1, "registrar forgot failed teardown");
    }

    private static async Task UpdateTeardownFailure()
    {
        await using var f = new Fixture();
        f.Fetch.Enqueue("alpha", "v1", "old");
        await f.Manager.InstallAsync(new() { Source = "fake" });
        f.Host.FailStop = true;
        f.Fetch.Enqueue("alpha", "v2", "new");
        Check(!(await f.Manager.UpdateAsync("alpha")).IsSuccess, "failed old teardown reported update success");
        Check(f.Host.Starts == 1 && f.Host.Running.Contains("plugin:alpha:old"), "candidate started despite failed old teardown");
        Check(f.Store.Find("alpha")?.ResolvedVersion == "v1", "failed teardown changed receipt");
        Check(File.ReadAllText(Path.Combine(f.PluginDir, "payload.txt")) == "v1", "failed teardown changed files");
    }

    private static async Task AwaitedBoundaries()
    {
        await using var f = new Fixture();
        f.Host.StartRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Fetch.Enqueue("alpha", "v1", "one");
        var installing = f.Manager.InstallAsync(new() { Source = "fake" });
        await f.Host.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(!installing.IsCompleted, "install did not await activation");
        f.Host.StartRelease.SetResult();
        Check((await installing).IsSuccess, "awaited install failed");
        f.Host.StopRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var removing = f.Manager.RemoveAsync("alpha");
        await f.Host.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(!removing.IsCompleted, "remove did not await teardown");
        Check(f.Store.Find("alpha") is not null && File.Exists(Path.Combine(f.PluginDir, "payload.txt")), "remove deleted before teardown completed");
        f.Host.StopRelease.SetResult();
        Check((await removing).IsSuccess && f.Host.Running.Count == 0, "awaited removal failed");
    }

    private static async Task StrictManagerRejectsMissingTransport()
    {
        await using var manager = new McpServerManager();
        var host = new McpServerManagerHost(manager);
        try { await host.StartServerAsync("plugin:alpha:invalid", new McpServerConfig(), true); }
        catch (InvalidOperationException)
        {
            Check(manager.GetClients().Count == 0, "invalid transport created a client");
            return;
        }
        throw new InvalidOperationException("existing manager host reported absent transport as successful startup");
    }

    private static async Task MissingExecutableManager()
    {
        await using var manager = new McpServerManager();
        var host = new McpServerManagerHost(manager);
        // A nonexistent absolute directory prevents PATH lookup or a shell launch on either OS.
        var command = Path.Combine(Path.GetTempPath(), "botnexus-3347-missing", Guid.NewGuid().ToString("N"), "missing-server.exe");
        Check(!File.Exists(command) && !Directory.Exists(Path.GetDirectoryName(command)), "missing executable unexpectedly exists");
        Exception? failure = null;
        try { await host.StartServerAsync("plugin:alpha:missing", new McpServerConfig { Command = command }, true); }
        catch (Exception ex) { failure = ex; }
        Check(failure is System.ComponentModel.Win32Exception, "launch failure was replaced by cleanup failure: " + failure?.GetType().Name);
        Check(manager.GetClients().Count == 0, "never-started process retained manager ownership");
        Check((await host.StopServersAsync(_ => true)).Count == 0, "never-started process required another stop");
    }

    private static async Task MissingExecutableInstall()
    {
        await using var manager = new McpServerManager();
        await using var f = new Fixture(serverHost: new McpServerManagerHost(manager));
        f.Fetch.Command = Path.Combine(f.PluginDir, "absent-directory", "missing-server.exe");
        Check(!File.Exists(f.Fetch.Command), "missing executable unexpectedly exists");
        f.Fetch.Enqueue("alpha", "v1", "missing");
        var result = await f.Manager.InstallAsync(new() { Source = "fake" });
        Check(!result.IsSuccess, "missing executable reported successful install");
        Check(f.Store.Find("alpha") is null, "never-started process retained failed-install receipt");
        Check(!File.Exists(Path.Combine(f.PluginDir, "payload.txt")), "never-started process retained failed-install files");
        Check(f.Registrar.GetRegisteredServerNames("alpha").Count == 0 && manager.GetClients().Count == 0, "never-started process retained registrar/manager ownership");
        // Cleanup is idempotent, and a repeated failed install must not become stuck as installed.
        await f.Registrar.UnregisterAsync("alpha");
        f.Fetch.Enqueue("alpha", "v1", "missing");
        Check(!(await f.Manager.InstallAsync(new() { Source = "fake" })).IsSuccess, "repeated missing executable install succeeded");
        Check(f.Store.Find("alpha") is null && manager.GetClients().Count == 0, "repeated launch failure retained recovery ownership");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task ExpectCancellation(Func<Task<PluginOperationResult>> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("expected cancellation was swallowed");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "botnexus-3347", Guid.NewGuid().ToString("N"));
        private readonly ServiceProvider _provider;
        public FakeHost Host { get; } = new();
        public FakeFetcher Fetch { get; } = new();
        public PluginLifecycleManager Manager { get; }
        public PluginStateStore Store { get; }
        public PluginMcpServerRegistrar Registrar { get; }
        public string PluginDir => Manager.GetPluginDirectory("alpha");
        public string StagingRoot => Path.Combine(_root, "isolated-staging");
        public Fixture(bool deny = false, IMcpServerHost? serverHost = null)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IFileSystem, FileSystem>();
            services.AddSingleton(new BotNexusHome(new FileSystem(), _root, _root));
            services.AddSingleton<IPluginSourceFetcher>(Fetch);
            var host = serverHost ?? Host;
            services.AddSingleton<IMcpServerHost>(host);
            services.AddSingleton<IPluginTrustEvaluator>(new Trust(deny));
            Registrar = new PluginMcpServerRegistrar(host, new Trust(deny));
            services.AddSingleton(Registrar);
            new PluginLifecycleServiceContributor().ConfigureServices(services);
            _provider = services.BuildServiceProvider();
            Manager = _provider.GetRequiredService<PluginLifecycleManager>();
            var staging = typeof(PluginLifecycleManager).GetProperty("StagingRoot", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("isolated staging seam missing");
            staging.SetValue(Manager, StagingRoot);
            Store = _provider.GetRequiredService<PluginStateStore>();
        }
        public async ValueTask DisposeAsync()
        {
            await _provider.DisposeAsync();
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class Trust(bool deny) : IPluginTrustEvaluator
    {
        public PluginTrustMode Mode => deny ? PluginTrustMode.Enforce : PluginTrustMode.Disabled;
        public PluginTrustDecision Evaluate(string pluginName, string pluginDirectory) =>
            deny ? PluginTrustDecision.Deny("content hash mismatch") : PluginTrustDecision.Trust;
    }

    private sealed class FakeHost : IMcpServerHost
    {
        public HashSet<string> Running { get; } = new(StringComparer.Ordinal);
        public int Starts { get; private set; }
        public int Stops { get; private set; }
        public int FailStart { get; set; }
        public int CancelStart { get; set; }
        public int FailAfterStart { get; set; }
        public bool FailStop { get; set; }
        public TaskCompletionSource StartEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource StopEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? StartRelease { get; set; }
        public TaskCompletionSource? StopRelease { get; set; }
        public Func<Task<IReadOnlyList<string>>>? StopBoundary { get; set; }
        public async Task<IReadOnlyList<IAgentTool>> StartServerAsync(string serverId, McpServerConfig serverConfig, bool useToolPrefix, CancellationToken cancellationToken = default)
        {
            Starts++;
            StartEntered.TrySetResult();
            if (StartRelease is not null) await StartRelease.Task.WaitAsync(cancellationToken);
            if (Starts == CancelStart) throw new OperationCanceledException();
            if (Starts == FailStart || (FailAfterStart > 0 && Starts >= FailAfterStart)) throw new IOException(SensitiveDiagnostic);
            Running.Add(serverId);
            return [];
        }
        public async Task<IReadOnlyList<string>> StopServersAsync(Func<string, bool> predicate, CancellationToken cancellationToken = default)
        {
            Stops++;
            StopEntered.TrySetResult();
            if (StopRelease is not null) await StopRelease.Task.WaitAsync(cancellationToken);
            Check(!cancellationToken.IsCancellationRequested, "cleanup used cancelled token");
            if (FailStop) throw new IOException(SensitiveDiagnostic);
            if (StopBoundary is not null) await StopBoundary();
            var stopped = Running.Where(predicate).ToArray();
            Running.ExceptWith(stopped);
            return stopped;
        }
    }

    private sealed class FakeFetcher : IPluginSourceFetcher
    {
        private readonly Queue<(string Name, string Version, string? Servers, bool Explicit)> _queue = new();
        public string Command { get; set; } = "never-executed";
        public string? ExtraFile { get; set; }
        public bool FailFetch { get; set; }
        public void Enqueue(string name, string version, string? servers, bool explicitPath = false) => _queue.Enqueue((name, version, servers, explicitPath));
        public Task<PluginFetchResult> FetchAsync(string source, string? reference, string stagingDirectory, CancellationToken cancellationToken = default)
        {
            if (FailFetch) throw new IOException(SensitiveDiagnostic);
            var item = _queue.Dequeue();
            var manifest = new Dictionary<string, object> { ["name"] = item.Name };
            if (item.Explicit) manifest["mcpServers"] = "custom/servers.json";
            Write(".botnexus-plugin/plugin.json", JsonSerializer.Serialize(manifest));
            Write("payload.txt", item.Version);
            if (item.Servers is not null)
            {
                var servers = item.Servers.Split(',').ToDictionary(n => n, _ => new { command = Command });
                Write(item.Explicit ? "custom/servers.json" : ".mcp.json", JsonSerializer.Serialize(new { mcpServers = servers }));
            }
            if (ExtraFile is not null && ExtraFile != "custom/servers.json") Write(ExtraFile, "candidate");
            return Task.FromResult(new PluginFetchResult(item.Version));
            void Write(string relative, string text)
            {
                var path = Path.Combine(stagingDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new InvalidOperationException("missing parent"));
                File.WriteAllText(path, text);
            }
        }
    }
}
