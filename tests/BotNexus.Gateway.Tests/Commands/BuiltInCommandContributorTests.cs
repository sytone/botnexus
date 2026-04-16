using System.Reflection;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Extensions;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Commands;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests.Commands;

public sealed class BuiltInCommandContributorTests
{
    [Fact]
    public void GetCommands_ReturnsExpectedBuiltInCommands()
    {
        var contributor = CreateContributor(out _, out _, out _);

        var commands = InvokeGetCommands(contributor);

        commands.Select(command => command.Name).Should().Contain(
            ["/help", "/status", "/agents", "/new", "/reset"]);
    }

    [Fact]
    public void GetCommands_ResetHasClientSideOnlyTrue()
    {
        var contributor = CreateContributor(out _, out _, out _);

        var reset = InvokeGetCommands(contributor).Single(command => command.Name == "/reset");

        reset.ClientSideOnly.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_Help_ReturnsCommandList()
    {
        var contributor = CreateContributor(out _, out _, out _);

        var result = await InvokeExecuteAsync(contributor, "/help", "/help");

        result.IsError.Should().BeFalse();
        result.Body.Should().Contain("/help");
    }

    [Fact]
    public async Task ExecuteAsync_Status_ReturnsGatewayStatus()
    {
        var contributor = CreateContributor(out _, out _, out _);

        var result = await InvokeExecuteAsync(contributor, "/status", "/status");

        result.IsError.Should().BeFalse();
        result.Body.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ExecuteAsync_Agents_ReturnsAgentList()
    {
        var contributor = CreateContributor(out var registry, out _, out _);
        registry.Setup(value => value.GetAll()).Returns(
        [
            new AgentDescriptor
            {
                AgentId = AgentId.From("nova"),
                DisplayName = "Nova",
                ModelId = "claude-sonnet-4.5",
                ApiProvider = "copilot"
            }
        ]);

        var result = await InvokeExecuteAsync(contributor, "/agents", "/agents");

        result.IsError.Should().BeFalse();
        result.Body.ToLowerInvariant().Should().Contain("nova");
    }

    [Fact]
    public async Task ExecuteAsync_Reset_ReturnsClientSideError()
    {
        var contributor = CreateContributor(out _, out _, out _);

        var result = await InvokeExecuteAsync(contributor, "/reset", "/reset");

        result.IsError.Should().BeTrue();
        result.Body.ToLowerInvariant().Should().Contain("client");
    }

    [Fact]
    public async Task ExecuteAsync_UnknownCommand_ReturnsError()
    {
        var contributor = CreateContributor(out _, out _, out _);

        var result = await InvokeExecuteAsync(contributor, "/unknown", "/unknown");

        result.IsError.Should().BeTrue();
    }

    private static object CreateContributor(
        out Mock<IAgentRegistry> registry,
        out Mock<ISessionStore> sessionStore,
        out Mock<IServiceProvider> serviceProvider)
    {
        registry = new Mock<IAgentRegistry>();
        registry.Setup(value => value.GetAll()).Returns([]);

        sessionStore = new Mock<ISessionStore>();
        sessionStore
            .Setup(value => value.ListAsync(It.IsAny<AgentId?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(value => value.GetAllInstances()).Returns([]);
        serviceProvider = new Mock<IServiceProvider>();

        var contributorType = ResolveType(
            "BotNexus.Gateway.Commands.BuiltInCommandContributor, BotNexus.Gateway");
        var contributor = CreateInstance(contributorType, new Dictionary<Type, object>
        {
            [typeof(IAgentRegistry)] = registry.Object,
            [typeof(IAgentSupervisor)] = supervisor.Object,
            [typeof(ISessionStore)] = sessionStore.Object,
            [typeof(IServiceProvider)] = serviceProvider.Object
        });

        if (contributor is ICommandContributor commandContributor)
        {
            var commandRegistry = new CommandRegistry([commandContributor], NullLogger<CommandRegistry>.Instance);
            serviceProvider.Setup(value => value.GetService(typeof(CommandRegistry))).Returns(commandRegistry);
        }

        return contributor;
    }

    private static IReadOnlyList<CommandDescriptor> InvokeGetCommands(object contributor)
    {
        var method = contributor.GetType().GetMethod("GetCommands", BindingFlags.Instance | BindingFlags.Public);
        method.Should().NotBeNull("BuiltInCommandContributor must expose GetCommands.");
        return (IReadOnlyList<CommandDescriptor>)method!.Invoke(contributor, null)!;
    }

    private static async Task<CommandResult> InvokeExecuteAsync(object contributor, string commandName, string rawInput)
    {
        var method = contributor.GetType().GetMethod(
            "ExecuteAsync",
            BindingFlags.Instance | BindingFlags.Public,
            [typeof(string), typeof(CommandExecutionContext), typeof(CancellationToken)]);
        method.Should().NotBeNull("BuiltInCommandContributor must expose ExecuteAsync.");

        var context = new CommandExecutionContext
        {
            RawInput = rawInput,
            HomeDirectory = @"Q:\repos\botnexus"
        };

        var task = (Task<CommandResult>)method!.Invoke(contributor, [commandName, context, CancellationToken.None])!;
        return await task;
    }

    private static object CreateInstance(Type type, IReadOnlyDictionary<Type, object> overrides)
    {
        foreach (var constructor in type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                     .OrderByDescending(value => value.GetParameters().Length))
        {
            if (!TryBuildArguments(constructor.GetParameters(), overrides, out var arguments))
                continue;

            try
            {
                return constructor.Invoke(arguments);
            }
            catch
            {
                // Try the next constructor shape.
            }
        }

        throw new InvalidOperationException($"Unable to construct {type.FullName} for testing.");
    }

    private static bool TryBuildArguments(
        IReadOnlyList<ParameterInfo> parameters,
        IReadOnlyDictionary<Type, object> overrides,
        out object?[] arguments)
    {
        arguments = new object?[parameters.Count];
        for (var index = 0; index < parameters.Count; index++)
        {
            var parameter = parameters[index];
            if (overrides.TryGetValue(parameter.ParameterType, out var overrideValue))
            {
                arguments[index] = overrideValue;
                continue;
            }

            if (TryCreateDefault(parameter.ParameterType, out var value))
            {
                arguments[index] = value;
                continue;
            }

            if (parameter.HasDefaultValue)
            {
                arguments[index] = parameter.DefaultValue;
                continue;
            }

            return false;
        }

        return true;
    }

    private static bool TryCreateDefault(Type parameterType, out object? value)
    {
        if (parameterType == typeof(string))
        {
            value = @"Q:\repos\botnexus";
            return true;
        }

        if (parameterType.IsGenericType &&
            parameterType.GetGenericTypeDefinition() == typeof(ILogger<>))
        {
            var loggerType = typeof(NullLogger<>).MakeGenericType(parameterType.GetGenericArguments()[0]);
            value = loggerType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)!.GetValue(null);
            return true;
        }

        if (parameterType.IsInterface)
        {
            var mock = Activator.CreateInstance(typeof(Mock<>).MakeGenericType(parameterType))!;
            var objectProperty = mock.GetType()
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .First(property => string.Equals(property.Name, "Object", StringComparison.Ordinal) &&
                                   parameterType.IsAssignableFrom(property.PropertyType));
            value = objectProperty.GetValue(mock);
            return true;
        }

        if (parameterType.IsValueType)
        {
            value = Activator.CreateInstance(parameterType);
            return true;
        }

        if (parameterType.GetConstructor(Type.EmptyTypes) is not null)
        {
            value = Activator.CreateInstance(parameterType);
            return true;
        }

        value = null;
        return false;
    }

    private static Type ResolveType(string assemblyQualifiedName)
        => Type.GetType(assemblyQualifiedName)
           ?? throw new InvalidOperationException($"Type not found: {assemblyQualifiedName}");
}
