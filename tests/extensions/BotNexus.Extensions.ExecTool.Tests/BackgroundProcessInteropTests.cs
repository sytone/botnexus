using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Gateway.Extensions;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Domain.Primitives;

namespace BotNexus.Extensions.ExecTool.Tests;

/// <summary>Exercises the production ALC implementation, not a test registry or PID attachment.</summary>
[Collection(ExecToolBackgroundRegistryCollection.Name)]
public sealed class BackgroundProcessInteropTests
{
    [Fact]
    public async Task BackgroundPid_IsImmediatelyManageableAcrossExtensionLoadContexts()
    {
        var execPath = Path.Combine(AppContext.BaseDirectory, "BotNexus.Extensions.ExecTool.dll");
        var processPath = Path.Combine(AppContext.BaseDirectory, "BotNexus.Extensions.ProcessTool.dll");
        var execContext = new ExtensionAssemblyLoadContext(execPath);
        var processContext = new ExtensionAssemblyLoadContext(processPath);
        var pid = 0;
        var owner = "interop-" + Guid.NewGuid().ToString("N");
        try
        {
            var execAssembly = execContext.LoadFromAssemblyPath(execPath);
            var processAssembly = processContext.LoadFromAssemblyPath(processPath);
            AssemblyLoadContext.GetLoadContext(execAssembly).ShouldBeSameAs(execContext);
            AssemblyLoadContext.GetLoadContext(processAssembly).ShouldBeSameAs(processContext);
            var exec = await Contribute(execAssembly, "BotNexus.Extensions.ExecTool.ExecToolContributor", owner);
            var process = await Contribute(processAssembly, "BotNexus.Extensions.ProcessTool.ProcessToolContributor", owner);
            var foreign = await Contribute(processAssembly, "BotNexus.Extensions.ProcessTool.ProcessToolContributor", owner + "-other");
            string[] command = OperatingSystem.IsWindows()
                ? ["pwsh", "-NoProfile", "-Command", "[Console]::WriteLine('ready'); $line = [Console]::ReadLine(); [Console]::WriteLine('echo:' + $line)"]
                : ["/bin/sh", "-c", "printf 'ready\\n'; read line; printf 'echo:%s\\n' \"$line\""];
            var args = await exec.PrepareArgumentsAsync(new Dictionary<string, object?>
            {
                ["command"] = command,
                ["background"] = true,
            });
            var launched = await exec.ExecuteAsync("launch", args);
            using var json = JsonDocument.Parse(launched.Content[0].Value);
            pid = json.RootElement.GetProperty("pid").GetInt32();
            (await Call(process, "status", pid)).ShouldContain("Status: running");
            foreach (var action in new[] { "status", "output", "input", "kill" })
                (await Call(foreign, action, pid, "intrusion\n")).ShouldContain("No tracked process");
            (await Call(foreign, "list", pid)).ShouldNotContain(pid.ToString());
            (await Call(process, "input", pid, "hello\n")).ShouldContain("Sent 6 characters");
            (await Call(process, "status", pid, timeoutMs: 30_000)).ShouldContain("Status: exited");
            var output = await Call(process, "output", pid);
            output.ShouldContain("ready");
            output.ShouldContain("echo:hello");
            (await Call(process, "status", Environment.ProcessId)).ShouldContain("No tracked process");
            (await Call(process, "kill", Environment.ProcessId)).ShouldContain("No tracked process");
        }
        finally
        {
            if (pid > 0)
            {
                try
                {
                    using var child = Process.GetProcessById(pid);
                    if (!child.HasExited) child.Kill(entireProcessTree: true);
                    child.WaitForExit(5_000);
                }
                catch (ArgumentException) { }
            }
            BackgroundProcessRegistry.Instance.Clear(owner);
            execContext.Unload();
            processContext.Unload();
        }
    }

    private static async Task<IAgentTool> Contribute(Assembly assembly, string name, string owner)
    {
        var type = assembly.GetType(name, throwOnError: true);
        type.ShouldNotBeNull();
        var instance = name.Contains("ExecTool", StringComparison.Ordinal)
            ? Activator.CreateInstance(type, new object?[] { null })
            : Activator.CreateInstance(type);
        var contributor = instance.ShouldBeAssignableTo<IAgentToolContributor>();
        contributor.ShouldNotBeNull();
        var context = new AgentToolContributionContext(
            new AgentDescriptor { AgentId = AgentId.From(owner), DisplayName = owner, ModelId = "test", ApiProvider = "test", ToolIds = [] },
            new AgentExecutionContext { SessionId = SessionId.Create() }, Path.GetTempPath(), new AllowPaths(), null,
            (_, _) => Task.FromResult<string?>(null));
        return (await contributor.ContributeAsync(context)).Tools.ShouldHaveSingleItem();
    }

    private sealed class AllowPaths : IPathValidator
    {
        public bool CanRead(string absolutePath) => true;
        public bool CanWrite(string absolutePath) => true;
        public string? ValidateAndResolve(string rawPath, FileAccessMode mode) => rawPath;
    }

    private static async Task<string> Call(IAgentTool tool, string action, int pid, string? content = null, int timeoutMs = 0)
    {
        var result = await tool.ExecuteAsync("manage", new Dictionary<string, object?>
        {
            ["action"] = action, ["pid"] = pid, ["content"] = content, ["timeoutMs"] = timeoutMs, ["tail"] = 0,
        });
        return result.Content[0].Value;
    }
}
