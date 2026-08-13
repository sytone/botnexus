using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.RegularExpressions;
using BotNexus.Cli.Commands;
using Shouldly;

namespace BotNexus.Cli.Tests.Commands;

/// <summary>
/// Pins the default bind address of the CLI port-availability probe (#2797).
///
/// <para>
/// The decision this repository owns is a single default: when no
/// <c>bindAddress</c> is supplied, <see cref="ServeCommand.IsPortAvailable(int, IPAddress?)"/>
/// probes the wildcard address, so the probe interface matches the interface the
/// gateway actually binds (#1536). Nothing asserted that default in its own right.
/// It was covered only incidentally, by tests that opened a real wildcard socket and
/// observed that the kernel refuses a second bind - assertions about operating-system
/// semantics that neither .NET nor this repository implements.
/// </para>
/// <para>
/// A wildcard bind also has a permanent side effect on Windows: the firewall prompts
/// and writes an inbound Allow rule scoped to the test host binary, one pair per
/// worktree path. All 34 BotNexus firewall rules on a developer machine originated
/// from this one test class; the other 54 test projects bind loopback and prompt for
/// nothing.
/// </para>
/// <para>
/// So the default is asserted structurally instead, by reading the compiled method
/// body. That names the default as its own subject, performs no bind at all, and goes
/// red by name if the default is changed to <see cref="IPAddress.Loopback"/>.
/// </para>
/// </summary>
public sealed class PortProbeDefaultAddressTests
{
    /// <summary>
    /// AC1 / AC5. The probe must fall back to the wildcard address, and must not fall
    /// back to loopback. Both directions are asserted: naming only the wildcard field
    /// would still pass a body that read both, and forbidding only loopback would pass
    /// a body that read neither.
    /// </summary>
    [Fact]
    public void IsPortAvailable_DefaultsToWildcardAddress_WithoutBinding()
    {
        var probe = typeof(ServeCommand).GetMethod(
            nameof(ServeCommand.IsPortAvailable),
            BindingFlags.Public | BindingFlags.Static,
            [typeof(int), typeof(IPAddress)]);

        probe.ShouldNotBeNull("ServeCommand.IsPortAvailable(int, IPAddress?) must exist for the default to be pinned.");

        var fields = IlReader.ReadStaticFieldReads(probe!)
            .Where(f => f.DeclaringType == typeof(IPAddress))
            .Select(f => f.Name)
            .ToList();

        fields.ShouldContain(
            nameof(IPAddress.Any),
            "IsPortAvailable must default to the wildcard address so the probe covers the same "
            + "interface the gateway binds (#1536). Read fields on IPAddress: "
            + string.Join(", ", fields));

        fields.ShouldNotContain(
            nameof(IPAddress.Loopback),
            "A loopback-scoped default mis-detects an occupant holding the port on the wildcard "
            + "address or a non-loopback NIC - the exact defect #1536 was filed for.");
    }

    /// <summary>
    /// The delegation from <c>UpdateCommand</c> is what keeps all three CLI call sites on one
    /// probe. It was previously covered by a test that opened a wildcard occupant; the call
    /// itself is what matters and is observable without any socket.
    /// </summary>
    [Fact]
    public void UpdateCommand_IsPortAvailable_DelegatesToServeCommandProbe()
    {
        var delegating = typeof(UpdateCommand).GetMethod(
            "IsPortAvailable",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static,
            [typeof(int)]);

        delegating.ShouldNotBeNull("UpdateCommand.IsPortAvailable(int) must exist.");

        var called = IlReader.ReadCalledMethods(delegating!)
            .Where(m => m.DeclaringType == typeof(ServeCommand))
            .Select(m => m.Name)
            .ToList();

        called.ShouldContain(
            nameof(ServeCommand.IsPortAvailable),
            "UpdateCommand must share the wildcard-aligned probe rather than carrying a second "
            + "implementation that can drift. Called ServeCommand members: " + string.Join(", ", called));
    }

    /// <summary>
    /// AC2. No test in this project may open a wildcard-bound socket, because each one writes a
    /// permanent inbound firewall rule on Windows scoped to the worktree's test host path.
    /// </summary>
    /// <remarks>
    /// The patterns are assembled from fragments so this file cannot match itself: the literal
    /// forms it forbids never appear here unescaped.
    /// </remarks>
    [Fact]
    public void NoTestInThisProject_BindsAWildcardSocket()
    {
        var projectRoot = FindProjectRoot();
        var files = Directory.GetFiles(projectRoot, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .ToList();

        // Non-vacuity: a fence over an empty candidate set passes for the wrong reason.
        files.Count.ShouldBeGreaterThan(
            20,
            $"Expected to scan the BotNexus.Cli.Tests sources, found {files.Count} under {projectRoot}.");

        var wildcardField = "IPAddress" + @"\." + "Any";
        var patterns = new[]
        {
            // new TcpListener(IPAddress.Any, ...) / new Socket(...).Bind(new IPEndPoint(IPAddress.Any, ...))
            new Regex(@"new\s+TcpListener\s*\(\s*" + wildcardField, RegexOptions.None),
            new Regex(@"new\s+IPEndPoint\s*\(\s*" + wildcardField, RegexOptions.None),
            new Regex(@"\.Bind\s*\(\s*[^)]*" + wildcardField, RegexOptions.None),
            // The dotted-quad spelling of the same address, parsed at runtime.
            new Regex("IPAddress" + @"\.Parse\s*\(\s*""0" + @"\.0\.0\.0", RegexOptions.None),
        };

        var violations = new List<string>();
        foreach (var file in files)
        {
            var text = StripComments(File.ReadAllText(file));
            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (patterns.Any(p => p.IsMatch(lines[i])))
                {
                    violations.Add($"{Path.GetRelativePath(projectRoot, file)}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        violations.ShouldBeEmpty(
            "A wildcard socket bind creates a permanent Windows firewall rule per worktree (#2797). "
            + "Scope test sockets to IPAddress.Loopback and assert the probe's default structurally "
            + "instead. Violations:\n" + string.Join("\n", violations));
    }

    private static string StripComments(string source)
    {
        var noBlock = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(noBlock, @"//[^\r\n]*", string.Empty);
    }

    private static string FindProjectRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "BotNexus.Cli.Tests.csproj")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not locate BotNexus.Cli.Tests project root from the test base directory.");
    }
}

/// <summary>
/// Minimal IL reader: walks a method body opcode by opcode and resolves the metadata tokens of
/// static field reads and calls. Used to assert a default value that is chosen inside the method
/// body, where no reflectable parameter default exists and where exercising the behaviour would
/// require the very socket bind #2797 exists to remove.
/// </summary>
internal static class IlReader
{
    private static readonly Dictionary<short, OpCode> OpCodes1 = BuildOpCodeTable();

    public static IEnumerable<FieldInfo> ReadStaticFieldReads(MethodBase method)
    {
        foreach (var (opCode, token) in Walk(method))
        {
            if (opCode == System.Reflection.Emit.OpCodes.Ldsfld || opCode == System.Reflection.Emit.OpCodes.Ldsflda)
            {
                var field = SafeResolve(() => method.Module.ResolveField(
                    token,
                    method.DeclaringType?.GetGenericArguments(),
                    method is MethodInfo mi ? mi.GetGenericArguments() : null));
                if (field is not null)
                {
                    yield return field;
                }
            }
        }
    }

    public static IEnumerable<MethodBase> ReadCalledMethods(MethodBase method)
    {
        foreach (var (opCode, token) in Walk(method))
        {
            if (opCode == System.Reflection.Emit.OpCodes.Call || opCode == System.Reflection.Emit.OpCodes.Callvirt)
            {
                var called = SafeResolve(() => method.Module.ResolveMethod(
                    token,
                    method.DeclaringType?.GetGenericArguments(),
                    method is MethodInfo mi ? mi.GetGenericArguments() : null));
                if (called is not null)
                {
                    yield return called;
                }
            }
        }
    }

    private static T? SafeResolve<T>(Func<T?> resolve)
        where T : class
    {
        try
        {
            return resolve();
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static IEnumerable<(OpCode OpCode, int Token)> Walk(MethodBase method)
    {
        var body = method.GetMethodBody()
            ?? throw new InvalidOperationException($"{method.DeclaringType?.Name}.{method.Name} has no IL body to inspect.");
        var il = body.GetILAsByteArray()
            ?? throw new InvalidOperationException($"{method.DeclaringType?.Name}.{method.Name} exposes no IL bytes.");

        var position = 0;
        while (position < il.Length)
        {
            short value = il[position++];
            if (value == 0xFE)
            {
                value = (short)(0xFE00 | il[position++]);
            }

            if (!OpCodes1.TryGetValue(value, out var opCode))
            {
                // An unknown opcode means the walk has lost alignment; stop rather than
                // report a token read from the middle of an operand.
                yield break;
            }

            var operandSize = OperandSize(opCode, il, position);
            var token = operandSize == 4 && IsTokenOperand(opCode)
                ? BitConverter.ToInt32(il, position)
                : 0;

            yield return (opCode, token);
            position += operandSize;
        }
    }

    private static bool IsTokenOperand(OpCode opCode) =>
        opCode.OperandType is OperandType.InlineField
            or OperandType.InlineMethod
            or OperandType.InlineType
            or OperandType.InlineTok
            or OperandType.InlineString
            or OperandType.InlineSig;

    private static int OperandSize(OpCode opCode, byte[] il, int position) => opCode.OperandType switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI
            or OperandType.InlineMethod or OperandType.InlineSig or OperandType.InlineString
            or OperandType.InlineTok or OperandType.InlineType or OperandType.ShortInlineR => 4,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(il, position)),
        _ => throw new NotSupportedException($"Unhandled operand type {opCode.OperandType}."),
    };

    private static Dictionary<short, OpCode> BuildOpCodeTable()
    {
        var table = new Dictionary<short, OpCode>();
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is OpCode opCode)
            {
                table[opCode.Value] = opCode;
            }
        }

        return table;
    }
}
