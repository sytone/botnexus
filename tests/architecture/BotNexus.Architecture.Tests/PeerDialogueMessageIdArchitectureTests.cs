using System.Reflection;
using BotNexus.Domain.Primitives;

namespace BotNexus.Architecture.Tests;

public sealed class PeerDialogueMessageIdArchitectureTests
{
    [Fact]
    public void PeerDialogueMessageId_IsVogenValueObjectWithoutImplicitConversions()
    {
        var type = typeof(PeerDialogueMessageId);
        type.IsValueType.ShouldBeTrue();
        type.GetCustomAttributes().ShouldContain(attribute =>
            attribute.GetType().Name.StartsWith("ValueObjectAttribute", StringComparison.Ordinal));
        type.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.Name == "op_Implicit").ShouldBeEmpty();
    }
}
