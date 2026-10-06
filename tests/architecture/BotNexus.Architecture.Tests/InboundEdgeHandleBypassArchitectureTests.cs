using Mono.Cecil;
using Mono.Cecil.Cil;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Prevents public inbound user-message edges from bypassing the unified orchestrator and
/// invoking steer/interrupt directly on <c>IAgentHandle</c>.
/// </summary>
public sealed class InboundEdgeHandleBypassArchitectureTests
{
    private static readonly HashSet<string> ForbiddenMethods =
    [
        "SteerAsync",
        "InterruptAndSteerAsync"
    ];

    [Fact]
    public void InboundEdges_DoNotCallAgentHandleSteerOrInterruptDirectly()
    {
        var edgeTypes = new[]
        {
            typeof(BotNexus.Gateway.Api.Controllers.ChatController),
            typeof(BotNexus.Extensions.Channels.SignalR.GatewayHub),
            typeof(BotNexus.Extensions.Channels.Tui.TuiChannelAdapter)
        };

        var violations = edgeTypes
            .SelectMany(type => FindHandleViolations(type.Assembly.Location, type.FullName!))
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        violations.ShouldBeEmpty(
            "Inbound edge surfaces must express delivery intent through IInboundMessageOrchestrator; " +
            "only the dispatch layer may call IAgentHandle steer/interrupt methods.");
    }

    private static IEnumerable<string> FindHandleViolations(string assemblyPath, string fullTypeName)
    {
        using var assembly = AssemblyDefinition.ReadAssembly(assemblyPath);
        var type = assembly.MainModule.GetType(fullTypeName);
        type.ShouldNotBeNull();

        foreach (var method in type!.Methods.Where(method => method.HasBody))
        {
            foreach (var instruction in method.Body.Instructions)
            {
                if (instruction.OpCode.Code is not (Code.Call or Code.Callvirt)
                    || instruction.Operand is not MethodReference called
                    || called.DeclaringType.Name != "IAgentHandle"
                    || !ForbiddenMethods.Contains(called.Name))
                {
                    continue;
                }

                yield return $"{fullTypeName}.{method.Name} -> IAgentHandle.{called.Name}";
            }
        }
    }
}
