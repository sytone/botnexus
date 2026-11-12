using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;

/// <summary>Framework-only operation probe using the existing production extension loader.</summary>
internal static class PluginClosureProbe
{
    public static async Task RunAsync(string[] args)
    {
        if (args.Length != 6) throw new ArgumentException("Expected plugin-closure, host, MCP, Plugins.Api, order, synthetic root.");
        var hostPath = Path.GetFullPath(args[1]);
        var hostDirectory = Parent(hostPath);
        Require(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory) == hostDirectory, "Probe must run beside the pinned production host.");
        var paths = new[] { Path.GetFullPath(args[2]), Path.GetFullPath(args[3]) };
        Require(Path.GetFileName(paths[0]) == "BotNexus.Extensions.Mcp.dll", "Expected MCP entry identity.");
        Require(Path.GetFileName(paths[1]) == "BotNexus.Extensions.Plugins.Api.dll", "Expected Plugins.Api entry identity.");
        foreach (var (path, id) in new[] { (paths[0], "botnexus-mcp"), (paths[1], "botnexus-plugins-api") })
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(Parent(path), "botnexus-extension.json")));
            Require(manifest.RootElement.GetProperty("id").GetString() == id, "Unexpected extension manifest identity: " + path);
            Require(manifest.RootElement.GetProperty("entryAssembly").GetString() == Path.GetFileName(path), "Manifest entry mismatch.");
        }
        var gateway = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(hostDirectory, "BotNexus.Gateway.dll"));
        var loaderType = Type(gateway, "BotNexus.Gateway.Extensions.ExtensionAssemblyLoadContext");
        var order = args[4] switch
        {
            "mcp-first" => new[] { 0, 1 },
            "api-first" => new[] { 1, 0 },
            _ => throw new ArgumentException("Unknown plugin load order."),
        };
        var contexts = new List<AssemblyLoadContext>();
        try
        {
            foreach (var index in order)
            {
                var path = paths[index];
                var context = Activator.CreateInstance(loaderType, [path, false]) as AssemblyLoadContext
                    ?? throw new InvalidOperationException("Cannot construct production loader.");
                contexts.Add(context);
                var entry = context.LoadFromAssemblyPath(path);
                Require(AssemblyLoadContext.GetLoadContext(entry) == context, "Entry must execute in its own extension context.");
                Require(Path.GetFullPath(entry.Location) == path, "Entry came from another output.");
                if (index == 1)
                {
                    var contributor = Type(entry, "BotNexus.Extensions.Plugins.Api.PluginsEndpointContributor");
                    _ = Activator.CreateInstance(contributor) ?? throw new InvalidOperationException("Cannot construct actual API contributor.");
                    var contracts = AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName("BotNexus.Gateway.Abstractions"));
                    var endpointContract = Type(contracts, "BotNexus.Gateway.Abstractions.Extensions.IEndpointContributor");
                    Require(endpointContract.IsAssignableFrom(contributor), "API contributor must implement the actual host contract Type.");
                    CheckOrigin(endpointContract.Assembly, hostDirectory, path, context);
                }
                var plugins = context.LoadFromAssemblyName(new AssemblyName("BotNexus.Extensions.Plugins"));
                var parser = Activator.CreateInstance(Type(plugins, "BotNexus.Extensions.Plugins.PluginManifestParser"))
                    ?? throw new InvalidOperationException("Cannot construct actual Plugins parser.");
                var valid = Invoke(parser, "ParseManifest", "{\"name\":\"probe\"}");
                var invalid = Invoke(parser, "ParseManifest", "{\"name\":42}");
                Require(Property<bool>(valid, "IsValid") && !Property<bool>(invalid, "IsValid"), "Parser must enforce actual NJsonSchema validation.");
                Console.WriteLine("PARSER_VALID_AND_INVALID_OK " + entry.GetName().Name);
                if (index == 0) await ExerciseMcpAsync(entry, plugins, Path.GetFullPath(args[5]));
                // Force the actual parser's dependency graph, including Cron's production reference,
                // then verify origins/hash identity against this pinned host/private output only.
                var pending = new Queue<Assembly>(new[] { "BotNexus.Extensions.Plugins", "BotNexus.Cron", "NJsonSchema" }
                    .Select(name => context.LoadFromAssemblyName(new AssemblyName(name))));
                var visited = new HashSet<string>(StringComparer.Ordinal);
                while (pending.TryDequeue(out var assembly))
                {
                    if (!visited.Add(assembly.FullName ?? throw new InvalidOperationException("Missing assembly identity."))) continue;
                    CheckOrigin(assembly, hostDirectory, path, context);
                    // Framework references belong to the runtime; application/private dependencies
                    // are walked recursively so a transitive package cannot hide behind type presence.
                    if (IsFramework(assembly.Location)) continue;
                    foreach (var reference in assembly.GetReferencedAssemblies())
                        pending.Enqueue(context.LoadFromAssemblyName(reference));
                }
                foreach (var assembly in context.Assemblies) CheckOrigin(assembly, hostDirectory, path, context);
            }
            foreach (var assembly in AssemblyLoadContext.Default.Assemblies.Where(assembly => !assembly.IsDynamic))
                Require(!assembly.GetName().Name.AsSpan().StartsWith("xunit", StringComparison.OrdinalIgnoreCase)
                    && assembly.GetName().Name != "Shouldly", "Testhost dependencies leaked into isolated process.");
            Console.WriteLine("PLUGIN_CLOSURE_OK " + args[4]);
        }
        finally
        {
            foreach (var context in contexts)
                if (context.IsCollectible) context.Unload();
        }
    }

    private static async Task ExerciseMcpAsync(Assembly mcp, Assembly plugins, string syntheticRoot)
    {
        var installed = Path.Combine(syntheticRoot, "installed");
        var standalone = Path.Combine(syntheticRoot, "standalone");
        var foreign = Path.Combine(installed, "foreign");
        Directory.CreateDirectory(foreign);
        Directory.CreateDirectory(standalone);
        File.WriteAllText(Path.Combine(installed, "installed-plugins.json"), """[{"name":"foreign","source":"https://example.invalid/probe.git","resolvedVersion":"v1","manifestFormat":"Claude","skillsOnly":false,"installedAtUtc":"2026-01-01T00:00:00Z"}]""");
        // If policy ordering is broken this invalid declaration must fail rather than start anything.
        Directory.CreateDirectory(Path.Combine(foreign, ".botnexus-plugin"));
        File.WriteAllText(Path.Combine(foreign, ".botnexus-plugin", "mcp.json"), "opaque foreign declaration");
        var store = Activator.CreateInstance(Type(plugins, "BotNexus.Extensions.Plugins.Lifecycle.PluginStateStore"), [installed, null])
            ?? throw new InvalidOperationException("Cannot construct actual installed store.");
        var record = Invoke(store, "ResolveForExecution", "foreign", foreign);
        Require(Property<bool>(record, "SkillsOnly"), "Old false foreign record must remain skills-only.");
        var hostInterface = Type(mcp, "BotNexus.Extensions.Mcp.Plugins.IMcpServerHost");
        var host = DispatchProxy.Create(hostInterface, typeof(NoServerHost));
        var registrarType = Type(mcp, "BotNexus.Extensions.Mcp.Plugins.PluginMcpServerRegistrar");
        var authorityType = Type(mcp, "BotNexus.Extensions.Mcp.Plugins.NativePluginMcpAuthority");
        var authority = Activator.CreateInstance(authorityType, [store, standalone]) ?? throw new InvalidOperationException("Missing authority.");
        var registrar = Activator.CreateInstance(registrarType, [host, null, null, store, null, authority])
            ?? throw new InvalidOperationException("Cannot construct production registrar.");
        var refused = await InvokeTaskAsync(registrar, "RegisterAsync", "foreign", foreign, null, true, CancellationToken.None);
        Require(!Property<bool>(refused, "Registered") && Property<string>(refused, "SkippedReason").Contains("skills-only", StringComparison.Ordinal),
            "MCP actual registration must refuse foreign policy before declaration parsing.");
        Require(((NoServerHost)host).Calls == 0, "Foreign policy reached server host.");
        Console.WriteLine("POLICY_SKILLS_ONLY");
        var native = Path.Combine(standalone, "native");
        Directory.CreateDirectory(Path.Combine(native, ".botnexus-plugin"));
        File.WriteAllText(Path.Combine(native, ".botnexus-plugin", "plugin.json"), "{\"name\":\"native\"}");
        var admissionType = Type(mcp, "BotNexus.Extensions.Mcp.Plugins.NativePluginMcpAdmission");
        var admission = admissionType.GetMethod("Admit")?.Invoke(null, [native, authority])
            ?? throw new InvalidOperationException("Actual native admission failed.");
        var admitted = await InvokeTaskAsync(registrar, "RegisterNativeAsync", admission, null, true, CancellationToken.None);
        Require(Property<bool>(admitted, "Registered"), "Genuine native admission without server declarations must succeed.");
        Require(((NoServerHost)host).Calls == 0, "No external MCP server may be started by this probe.");
        Console.WriteLine("NATIVE_ADMISSION_OK");
    }

    private static void CheckOrigin(Assembly assembly, string host, string entryPath, AssemblyLoadContext context)
    {
        if (assembly.IsDynamic) return;
        var name = assembly.GetName().Name ?? throw new InvalidOperationException("Missing assembly identity.");
        var actual = Path.GetFullPath(assembly.Location);
        if (IsFramework(actual)) return;
        var hostCopy = Path.Combine(host, name + ".dll");
        var expected = File.Exists(hostCopy) ? hostCopy : new AssemblyDependencyResolver(entryPath).ResolveAssemblyToPath(assembly.GetName())
            ?? throw new InvalidOperationException("Private dependency absent from entry resolver: " + name);
        Require(actual == Path.GetFullPath(expected), $"{name} resolved outside exact pinned output: {actual}; expected {expected}");
        Require(AssemblyLoadContext.GetLoadContext(assembly) == (File.Exists(hostCopy) ? AssemblyLoadContext.Default : context),
            name + " must unify with host when shipped there.");
        Require(SHA256.HashData(File.ReadAllBytes(actual)).AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(expected))), "Assembly content mismatch.");
        Console.WriteLine("RESOLVED " + name + " " + actual + " SHA256=" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(actual))));
    }

    private static bool IsFramework(string location)
    {
        var framework = Parent(typeof(object).Assembly.Location);
        var aspnet = Path.Combine(Parent(Parent(framework)), "Microsoft.AspNetCore.App");
        return location.StartsWith(framework + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || location.StartsWith(aspnet + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static Type Type(Assembly assembly, string name) => assembly.GetType(name, true) ?? throw new InvalidOperationException("Missing " + name);
    private static string Parent(string path) => Path.GetDirectoryName(path) ?? throw new InvalidOperationException("No parent: " + path);
    private static object Invoke(object target, string method, params object?[] values) => target.GetType().GetMethod(method)?.Invoke(target, values)
        ?? throw new InvalidOperationException("Missing method/result: " + method);
    private static T Property<T>(object target, string name) => (T)(target.GetType().GetProperty(name)?.GetValue(target)
        ?? throw new InvalidOperationException("Missing property: " + name));
    private static async Task<object> InvokeTaskAsync(object target, string method, params object?[] values)
    {
        var task = Invoke(target, method, values) as Task ?? throw new InvalidOperationException("Expected task.");
        await task;
        return task.GetType().GetProperty("Result")?.GetValue(task) ?? throw new InvalidOperationException("Missing task result.");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

/// <summary>Fail-sensitive sentinel: any server lifecycle call fails without network or process execution.</summary>
public class NoServerHost : DispatchProxy
{
    public int Calls { get; private set; }
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        Calls++;
        throw new InvalidOperationException("Unexpected MCP server lifecycle call: " + targetMethod?.Name);
    }
}
