using System.Reflection;
using BotNexus.Domain.Primitives;

namespace BotNexus.Extensions.Channels.Agent365.Tests;

public sealed class Agent365ChannelAddressTests
{
    private delegate bool TryParseAddress(ChannelAddress address, out string conversationId, out string? serviceUrl);

    [Theory]
    [InlineData("conv-1", "https://svc.example.com/x/", "conv-1|svc:https://svc.example.com/x/")]
    [InlineData("conv-2", null, "conv-2")]
    [InlineData(" conv-3 \t", " https://svc.example.com/x/ \t", " conv-3 \t|svc: https://svc.example.com/x/ \t")]
    public void Create_ConversationAndOptionalServiceUrl_PreservesExactWireFormatAndRoundTrips(
        string conversationId, string? serviceUrl, string expectedWire)
    {
        var address = Agent365ChannelAddress.Create(conversationId, serviceUrl);

        address.Value.ShouldBe(expectedWire);
        Agent365ChannelAddress.TryParse(address, out var parsedConversationId, out var parsedServiceUrl).ShouldBeTrue();
        parsedConversationId.ShouldBe(conversationId);
        parsedServiceUrl.ShouldBe(serviceUrl);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\r\n")]
    public void Create_NullOrWhitespaceServiceUrl_OmitsSuffix(string? serviceUrl)
    {
        var address = Agent365ChannelAddress.Create("conv-1", serviceUrl);

        address.Value.ShouldBe("conv-1");
        Agent365ChannelAddress.TryParse(address, out var conversationId, out var parsedServiceUrl).ShouldBeTrue();
        conversationId.ShouldBe("conv-1");
        parsedServiceUrl.ShouldBeNull();
    }

    [Theory]
    [InlineData("not a URL")]
    [InlineData("relative/path")]
    [InlineData("ftp://example.com/path?q=a:b/c")]
    [InlineData("https://example.com/|svc:remaining|svc:verbatim")]
    public void Create_ArbitraryServiceUrl_AcceptsAndPreservesVerbatim(string serviceUrl)
    {
        var address = Agent365ChannelAddress.Create("conv-1", serviceUrl);

        address.Value.ShouldBe("conv-1|svc:" + serviceUrl);
        Agent365ChannelAddress.TryParse(address, out var conversationId, out var parsedServiceUrl).ShouldBeTrue();
        conversationId.ShouldBe("conv-1");
        parsedServiceUrl.ShouldBe(serviceUrl);
    }

    [Fact]
    public void Create_NullConversationId_ThrowsExactArgumentNullExceptionWithParameterName()
    {
        var method = GetPublicStaticMethod("Create");
        Action act = () => method.Invoke(null, new object?[] { null, "https://svc.example.com/" });

        var wrapper = Should.Throw<TargetInvocationException>(act);
        var exception = wrapper.InnerException.ShouldBeOfType<ArgumentNullException>();
        exception.ParamName.ShouldBe("conversationId");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\r\n")]
    public void Create_EmptyOrWhitespaceConversationId_ThrowsExactArgumentExceptionWithParameterName(string conversationId)
    {
        Action act = () => Agent365ChannelAddress.Create(conversationId, null);

        var exception = Should.Throw<ArgumentException>(act);
        exception.GetType().ShouldBe(typeof(ArgumentException));
        exception.ParamName.ShouldBe("conversationId");
    }

    [Fact]
    public void TryParse_DefaultAddress_ReturnsFalseAndClearsOutputs()
    {
        var conversationId = "stale conversation";
        string? serviceUrl = "stale service URL";

        Agent365ChannelAddress.TryParse(default, out conversationId, out serviceUrl).ShouldBeFalse();

        conversationId.ShouldBe(string.Empty);
        serviceUrl.ShouldBeNull();
    }

    [Fact]
    public void TryParse_EmptyAddress_ReturnsFalseAndClearsOutputs()
    {
        foreach (var address in new[] { ChannelAddress.Empty, ChannelAddress.From(string.Empty) })
        {
            var conversationId = "stale conversation";
            string? serviceUrl = "stale service URL";

            Agent365ChannelAddress.TryParse(address, out conversationId, out serviceUrl).ShouldBeFalse();

            conversationId.ShouldBe(string.Empty);
            serviceUrl.ShouldBeNull();
        }
    }

    [Theory]
    [InlineData("|svc:https://svc.example.com/", "https://svc.example.com/")]
    [InlineData("|svc:first|svc:second", "first|svc:second")]
    [InlineData("|svc: \t", " \t")]
    [InlineData("|svc:", null)]
    public void TryParse_EmptyConversationPrefix_ReturnsFalseButPreservesSuffix(string wire, string? expectedServiceUrl)
    {
        var conversationId = "stale conversation";
        string? serviceUrl = "stale service URL";

        Agent365ChannelAddress.TryParse(ChannelAddress.From(wire), out conversationId, out serviceUrl).ShouldBeFalse();

        conversationId.ShouldBe(string.Empty);
        serviceUrl.ShouldBe(expectedServiceUrl);
    }

    [Fact]
    public void TryParse_EmptyServiceUrlSuffix_ReturnsTrueWithNullServiceUrl()
    {
        Agent365ChannelAddress.TryParse(ChannelAddress.From("conv-1|svc:"), out var conversationId, out var serviceUrl).ShouldBeTrue();

        conversationId.ShouldBe("conv-1");
        serviceUrl.ShouldBeNull();
    }

    [Theory]
    [InlineData("conv-1|SVC:https://svc.example.com/")]
    [InlineData("conv-1|Svc:https://svc.example.com/")]
    [InlineData("conv-1|svC:https://svc.example.com/")]
    [InlineData("conv-1|ſvc:https://svc.example.com/")]
    public void TryParse_NonOrdinalOrDifferentlyCasedSeparator_PreservesBareConversation(string wire)
    {
        Agent365ChannelAddress.TryParse(ChannelAddress.From(wire), out var conversationId, out var serviceUrl).ShouldBeTrue();

        conversationId.ShouldBe(wire);
        serviceUrl.ShouldBeNull();
    }

    [Theory]
    [InlineData("conv-1|svc:first|svc:second|svc:", "conv-1", "first|svc:second|svc:")]
    [InlineData("conv-1|SVC:prefix|svc:remaining", "conv-1|SVC:prefix", "remaining")]
    public void TryParse_MultipleSeparators_SplitsOnlyAtFirstOrdinalMatch(
        string wire, string expectedConversationId, string expectedServiceUrl)
    {
        Agent365ChannelAddress.TryParse(ChannelAddress.From(wire), out var conversationId, out var serviceUrl).ShouldBeTrue();

        conversationId.ShouldBe(expectedConversationId);
        serviceUrl.ShouldBe(expectedServiceUrl);
    }

    [Theory]
    [InlineData(" \t\r\n", " \t\r\n", null)]
    [InlineData(" conv-1 \t", " conv-1 \t", null)]
    [InlineData(" \t|svc: \r\n", " \t", " \r\n")]
    public void TryParse_WhitespaceInput_PreservesNonemptyPartsWithoutTrimming(
        string wire, string expectedConversationId, string? expectedServiceUrl)
    {
        Agent365ChannelAddress.TryParse(ChannelAddress.From(wire), out var conversationId, out var serviceUrl).ShouldBeTrue();

        conversationId.ShouldBe(expectedConversationId);
        serviceUrl.ShouldBe(expectedServiceUrl);
    }

    [Fact]
    public void Create_PublicStaticContract_HasExactSignatureAndConstructsAddress()
    {
        Func<string, string?, ChannelAddress> create = Agent365ChannelAddress.Create;
        var method = GetPublicStaticMethod("Create");
        method.ReturnType.ShouldBe(typeof(ChannelAddress));
        var parameters = method.GetParameters();
        parameters.Select(parameter => parameter.ParameterType).ToArray().ShouldBe(new[] { typeof(string), typeof(string) });
        parameters.Select(parameter => parameter.Name).ToArray().ShouldBe(new[] { "conversationId", "serviceUrl" });
        var nullability = new NullabilityInfoContext();
        nullability.Create(parameters[0]).ReadState.ShouldBe(NullabilityState.NotNull);
        nullability.Create(parameters[1]).ReadState.ShouldBe(NullabilityState.Nullable);

        create("contract-conversation", "arbitrary-service").Value.ShouldBe("contract-conversation|svc:arbitrary-service");
    }

    [Fact]
    public void TryParse_PublicStaticContract_HasExactSignatureAndParsesAddress()
    {
        TryParseAddress tryParse = Agent365ChannelAddress.TryParse;
        var method = GetPublicStaticMethod("TryParse");
        method.ReturnType.ShouldBe(typeof(bool));
        var parameters = method.GetParameters();
        parameters.Select(parameter => parameter.ParameterType).ToArray().ShouldBe(
            new[] { typeof(ChannelAddress), typeof(string).MakeByRefType(), typeof(string).MakeByRefType() });
        parameters.Select(parameter => parameter.Name).ToArray().ShouldBe(new[] { "address", "conversationId", "serviceUrl" });
        parameters[1].IsOut.ShouldBeTrue();
        parameters[2].IsOut.ShouldBeTrue();
        var nullability = new NullabilityInfoContext();
        nullability.Create(parameters[1]).WriteState.ShouldBe(NullabilityState.NotNull);
        nullability.Create(parameters[2]).WriteState.ShouldBe(NullabilityState.Nullable);

        tryParse(ChannelAddress.From("contract-conversation|svc:arbitrary-service"), out var conversationId, out var serviceUrl).ShouldBeTrue();
        conversationId.ShouldBe("contract-conversation");
        serviceUrl.ShouldBe("arbitrary-service");
    }

    [Fact]
    public void PublicApi_LegacyFactoryNames_AreAbsent()
    {
        var methods = typeof(Agent365ChannelAddress).GetMethods(BindingFlags.Public | BindingFlags.Static);

        methods.Any(method => method.Name == "Encode").ShouldBeFalse();
        methods.Any(method => method.Name == "TryDecode").ShouldBeFalse();
    }

    private static MethodInfo GetPublicStaticMethod(string name) =>
        typeof(Agent365ChannelAddress).GetMethod(name, BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
        ?? throw new InvalidOperationException($"Expected public static Agent365ChannelAddress.{name} method.");
}
