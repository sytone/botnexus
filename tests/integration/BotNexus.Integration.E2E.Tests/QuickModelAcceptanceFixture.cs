using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace BotNexus.Integration.E2E.Tests;

/// <summary>Opt-in support for a privately owned candidate; defaults of shared fixtures are unchanged.</summary>
public sealed partial class NewUserExperienceFixture
{
    public bool QuickModelAcceptance { get; init; }
    public const string QuickModelAgent = "save-model-4952";
    public const string FirstModel = "integration-mock-echo";
    public const string SecondModel = "integration-mock-save-next";
    public const string DisallowedModel = "integration-mock-save-disallowed";
    public string AcceptanceData => Path.Combine(SandboxRoot, "data");
    public int AcceptanceProcessId => _gateway?.ProcessId ?? throw new InvalidOperationException("Candidate not started.");

    private async Task ConfigureQuickModelAcceptanceAsync()
    {
        await RunCliAsync("provider", $"add --name integration-mock --api integration-mock --default-model {FirstModel} --model {FirstModel} --model {SecondModel} --model {DisallowedModel} --base-url \"{CatalogPath}\" --target \"{Home}\"");
        await RunCliAsync("provider", $"add --name conflicting-mock --api integration-mock --default-model conflicting-only --model conflicting-only --base-url \"{CatalogPath}\" --target \"{Home}\"");
        await RunCliAsync("agent", $"add {QuickModelAgent} --provider integration-mock --model {FirstModel} --display-name \"Authored quick model agent\" --description \"Preserve authored description\" --target \"{Home}\"");
        await SetAcceptanceValueAsync("allowedModels", new[] { FirstModel, SecondModel });
        await SetAcceptanceValueAsync("toolIds", new[] { "get_datetime" });
        await SetAcceptanceValueAsync("toolTimeoutSeconds", 73);
        await SetAcceptanceValueAsync("memory", new { enabled = false });
        await SetAcceptanceValueAsync("metadata", new { authoredProbe = new { nested = new object?[] { 1, null, "keep" } } });

        // init and the writes above use JSON until this supported, explicit opt-in imports it.
        // Enable only after provisioning so all authored values enter the canonical store.
        await RunCliAsync("config", $"store enable --target \"{Home}\"");
        var storePath = Path.Combine(Home, "config.sqlite");
        File.Exists(storePath).ShouldBeTrue("config store enable must create the canonical store beside config.json");

        // Deliberately seed an unmodelled future field in the canonical store while the candidate
        // is offline. No projection is edited and no runtime consumer/writer races this arrangement.
        // ReadWrite refuses to manufacture an empty database if enable did not create this path.
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = storePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync();
        await using (var verify = connection.CreateCommand())
        {
            verify.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('config_entries','config_revision');";
            Convert.ToInt64(await verify.ExecuteScalarAsync()).ShouldBe(2L, "CLI enable must initialize both canonical tables before offline seeding");
            verify.CommandText = "SELECT value FROM config_entries WHERE scope=0 AND scope_id='' AND key_path=$modelPath AND state=3;";
            verify.Parameters.AddWithValue("$modelPath", $"agents.{QuickModelAgent}.model");
            (await verify.ExecuteScalarAsync()).ShouldBe(JsonSerializer.Serialize(FirstModel), "CLI enable must import the authored agent model");
            verify.Parameters.Clear();
            verify.CommandText = "SELECT COUNT(*) FROM config_revision WHERE singleton=1;";
            Convert.ToInt64(await verify.ExecuteScalarAsync()).ShouldBe(1L, "CLI enable must initialize the revision singleton");
        }
        Log.Add($"[acceptance-store] verified CLI-enabled canonical path, schema, authored model and revision: {storePath}");
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "INSERT INTO config_entries(scope,scope_id,key_path,state,value) VALUES(0,'',$nestedPath,3,$nested),(0,'',$flagPath,3,'true'); UPDATE config_revision SET revision=revision+1 WHERE singleton=1;";
        command.Parameters.AddWithValue("$nestedPath", $"agents.{QuickModelAgent}.futureOpaque.nested");
        command.Parameters.AddWithValue("$flagPath", $"agents.{QuickModelAgent}.futureOpaque.flag");
        command.Parameters.AddWithValue("$nested", "[1,null,\"keep\"]");
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    private Task<ProcessRunner.ProcessResult> SetAcceptanceValueAsync(string field, object value)
    {
        var json = JsonSerializer.Serialize(value).Replace("\"", "\\\"");
        return RunCliAsync("config", $"set agents.{QuickModelAgent}.{field} \"{json}\" --target \"{Home}\"");
    }

    /// <summary>Stops only this fixture's attached process and verifies exit plus listener closure.</summary>
    public async Task StopQuickModelGatewayAsync()
    {
        if (!QuickModelAcceptance)
            throw new InvalidOperationException("Shared fixture restart is forbidden.");
        var gateway = _gateway ?? throw new InvalidOperationException("Candidate not started.");
        var processId = gateway.ProcessId;
        using var process = Process.GetProcessById(processId);
        Log.Add($"[acceptance-stop] pid={processId}\n{gateway.SnapshotCombined()}");
        await gateway.DisposeAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await process.WaitForExitAsync(deadline.Token);
        process.HasExited.ShouldBeTrue();
        await TestAwait.EventuallyAsync(async () =>
        {
            using var socket = new TcpClient();
            try
            {
                await socket.ConnectAsync("127.0.0.1", GatewayPort, deadline.Token);
                return false;
            }
            catch (SocketException)
            {
                return true;
            }
        }, "owned gateway listener to close", TimeSpan.FromSeconds(15), cancellationToken: deadline.Token);
        _gateway = null;
        Log.Add($"[acceptance-stop] confirmed exit pid={processId}");
    }

    /// <summary>Restarts the same candidate against unchanged owned home/data, without re-provisioning.</summary>
    public async Task RestartQuickModelGatewayAsync()
    {
        var previousId = AcceptanceProcessId;
        await StopQuickModelGatewayAsync();
        _gateway = ProcessRunner.StartBackground(CliExecutablePath,
            $"gateway start --attached --skip-build --source \"{RepoLocator.FindRepoRoot()}\" --target \"{Home}\" --port {GatewayPort}",
            environment: new Dictionary<string, string?>
            {
                ["BOTNEXUS_HOME"] = Home,
                ["BOTNEXUS_DATA_DIR"] = AcceptanceData,
                ["BOTNEXUS_MOCK_CATALOG"] = CatalogPath,
            });
        BotNexus.Integration.Testing.SandboxProcessGuard.RecordSandboxGateway(SandboxRoot, _gateway.ProcessId);
        _gateway.ProcessId.ShouldNotBe(previousId);
        (await WaitForGatewayReadyAsync(GatewayBaseUrl, GatewayReadyTimeout, _gateway)).ShouldBeTrue(_gateway.SnapshotCombined());
        Log.Add($"[acceptance-restart] old={previousId} new={_gateway.ProcessId}; same home/data");
    }
}
