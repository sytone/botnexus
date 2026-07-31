using System.Diagnostics;
using System.Text.Json.Nodes;
using Shouldly;

namespace BotNexus.Cli.Tests;

/// <summary>
/// Issue #2134, residual acceptance clauses:
/// <list type="number">
///   <item>"Two CLI processes mutating disjoint keys both persist, or one receives an explicit
///   concurrency conflict."</item>
///   <item>"CLI versus running-gateway writer composition is covered using temporary
///   <c>BOTNEXUS_HOME</c>."</item>
/// </list>
/// <para>
/// These are deliberately <em>not</em> in-process tests. <c>PlatformConfigWriter.WriteLock</c> is a
/// <c>static SemaphoreSlim</c>, which only ever coordinates threads within a single process; the
/// existing revision/CAS token is opt-in (<c>expectedRevision</c>) and is not passed by any CLI
/// mutation path, so neither mechanism constrains a second OS process. Proving the cross-process
/// property therefore requires genuinely separate processes: each test below spawns the real
/// <c>BotNexus.Cli</c> assembly as a child <c>dotnet</c> process against a temporary
/// <c>BOTNEXUS_HOME</c>.
/// </para>
/// </summary>
public sealed class CrossProcessConfigWriteTests : IDisposable
{
    private readonly string _home;

    public CrossProcessConfigWriteTests()
    {
        _home = Path.Combine(
            Path.GetTempPath(), "botnexus-xproc-config", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_home);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_home))
                Directory.Delete(_home, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a lingering child handle must not fail the suite.
        }
    }

    private string ConfigPath => Path.Combine(_home, "config.json");

    private static string CliAssemblyPath
    {
        get
        {
            var path = Path.Combine(AppContext.BaseDirectory, "BotNexus.Cli.dll");
            File.Exists(path).ShouldBeTrue($"CLI assembly not found next to the test assembly: {path}");
            return path;
        }
    }

    private Process StartCliSet(string key, string value, int? lockTimeoutMs = null)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        psi.ArgumentList.Add(CliAssemblyPath);
        psi.ArgumentList.Add("config");
        psi.ArgumentList.Add("set");
        psi.ArgumentList.Add(key);
        psi.ArgumentList.Add(value);

        // Clause 2: the child resolves its config purely from a temporary BOTNEXUS_HOME, so the
        // test can never touch the developer's real ~/.botnexus.
        psi.Environment["BOTNEXUS_HOME"] = _home;
        if (lockTimeoutMs is not null)
            psi.Environment["BOTNEXUS_CONFIG_LOCK_TIMEOUT_MS"] = lockTimeoutMs.Value.ToString();

        return Process.Start(psi)!;
    }

    private void SeedConfig()
        => File.WriteAllText(ConfigPath, """{"gateway":{"listenUrl":"http://localhost:5000"}}""");

    /// <summary>
    /// Clause 1. Several genuinely separate CLI processes each set a <em>disjoint</em> key. The
    /// acceptance criterion allows either outcome, so the assertion is the disjunction: for every
    /// writer, either its key is present in the committed document, or that writer exited non-zero
    /// having reported an explicit concurrency conflict. What must never happen is a writer
    /// reporting success while its key is absent - that is the silent lost update.
    /// </summary>
    [Fact]
    public async Task ConcurrentCliProcesses_DisjointKeys_BothPersistOrConflictExplicitly()
    {
        SeedConfig();

        var writers = new (string Key, string Value)[]
        {
            ("gateway.listenUrl", "http://localhost:5101"),
            ("gateway.defaultAgentId", "agent-2134"),
            ("gateway.logLevel", "Debug"),
            ("gateway.agentsDirectory", "agents-2134")
        };

        var processes = writers.Select(w => StartCliSet(w.Key, w.Value)).ToArray();
        var outputs = new (int Exit, string Output)[processes.Length];
        for (var i = 0; i < processes.Length; i++)
        {
            var stdout = await processes[i].StandardOutput.ReadToEndAsync();
            var stderr = await processes[i].StandardError.ReadToEndAsync();
            await processes[i].WaitForExitAsync();
            outputs[i] = (processes[i].ExitCode, stdout + stderr);
            processes[i].Dispose();
        }

        var root = JsonNode.Parse(await File.ReadAllTextAsync(ConfigPath))!.AsObject();

        for (var i = 0; i < writers.Length; i++)
        {
            var (key, value) = writers[i];
            var (exit, output) = outputs[i];
            var node = ReadDotted(root, key);
            var persisted = node is not null
                && string.Equals(node.GetValue<string>(), value, StringComparison.Ordinal);

            if (exit == 0)
            {
                persisted.ShouldBeTrue(
                    $"'{key}' reported success (exit 0) but its value is not in the committed "
                    + $"document - a silent lost update. Output: {output}");
            }
            else
            {
                output.ToLowerInvariant().Contains("concurren").ShouldBeTrue(
                    $"'{key}' failed without an explicit concurrency conflict. Output: {output}");
            }
        }
    }

    /// <summary>
    /// Clause 2. Composition of a CLI writer against a <em>running gateway</em> writer that already
    /// holds the cross-process advisory lock. The test process stands in for the gateway: it holds
    /// <c>config.json.lock</c> exclusively (<see cref="FileShare.None"/>) for the whole critical
    /// section, exactly as the writer must. The CLI child, given a short lock timeout, must not be
    /// able to sneak a read-modify-write past it: it must fail explicitly and leave the file byte
    /// for byte unchanged.
    /// </summary>
    [Fact]
    public async Task CliWrite_WhileGatewayHoldsCrossProcessLock_FailsExplicitly_AndLeavesFileIntact()
    {
        SeedConfig();
        var before = await File.ReadAllTextAsync(ConfigPath);

        var lockPath = SidecarLockPath();
        int exitCode;
        string output;

        // "Gateway" side: hold the advisory lock for the duration of its critical section.
        using (var gatewayLock = new FileStream(
            lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            using var child = StartCliSet("gateway.listenUrl", "http://localhost:5199", lockTimeoutMs: 750);
            var stdout = await child.StandardOutput.ReadToEndAsync();
            var stderr = await child.StandardError.ReadToEndAsync();
            await child.WaitForExitAsync();
            exitCode = child.ExitCode;
            output = stdout + stderr;
            gatewayLock.Flush();
        }

        exitCode.ShouldNotBe(0,
            $"CLI write proceeded while the gateway held the cross-process config lock. Output: {output}");
        output.ToLowerInvariant().Contains("concurren").ShouldBeTrue(
            $"Expected an explicit concurrency conflict. Output: {output}");
        (await File.ReadAllTextAsync(ConfigPath)).ShouldBe(before,
            "The blocked CLI write must leave the on-disk config untouched.");
    }

    /// <summary>
    /// Clause 2, the release half: once the gateway's critical section ends the CLI must succeed.
    /// Without this the previous test could be satisfied by a writer that is simply always broken.
    /// </summary>
    [Fact]
    public async Task CliWrite_AfterGatewayReleasesLock_Succeeds()
    {
        SeedConfig();

        var lockPath = SidecarLockPath();
        using (var gatewayLock = new FileStream(
            lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            // Held and released before the child is even started.
            gatewayLock.Flush();
        }

        using var child = StartCliSet("gateway.listenUrl", "http://localhost:5200", lockTimeoutMs: 5000);
        var stdout = await child.StandardOutput.ReadToEndAsync();
        var stderr = await child.StandardError.ReadToEndAsync();
        await child.WaitForExitAsync();

        child.ExitCode.ShouldBe(0, stdout + stderr);
        var root = JsonNode.Parse(await File.ReadAllTextAsync(ConfigPath))!.AsObject();
        ReadDotted(root, "gateway.listenUrl")!.GetValue<string>().ShouldBe("http://localhost:5200");
    }

    /// <summary>
    /// Mirrors <c>CrossProcessConfigLock.ResolveLockPath</c>: the sidecar lives in a
    /// <c>locks/</c> subdirectory so the config directory keeps its "config.json and nothing else"
    /// contract, which the ConfigDiskE2E durability suite pins.
    /// </summary>
    private string SidecarLockPath()
    {
        var lockDir = Path.Combine(Path.GetDirectoryName(ConfigPath)!, "locks");
        Directory.CreateDirectory(lockDir);
        return Path.Combine(lockDir, Path.GetFileName(ConfigPath) + ".lock");
    }

    private static JsonNode? ReadDotted(JsonObject root, string dotted)
    {
        JsonNode? current = root;
        foreach (var segment in dotted.Split('.'))
        {
            if (current is not JsonObject obj || !obj.TryGetPropertyValue(segment, out var next))
                return null;
            current = next;
        }

        return current;
    }
}
