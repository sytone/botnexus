using System.Reflection;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Extensions;
using BotNexus.Gateway.Commands;
using BotNexus.Gateway.Configuration;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests.Commands;

public sealed class CommandsControllerTests
{
    [Fact]
    public async Task GetCommands_ReturnsOkWithCommandList()
    {
        var controller = CreateController();
        var method = controller.GetType().GetMethod("GetCommands", BindingFlags.Instance | BindingFlags.Public);

        var actionResult = await InvokeAsActionResultAsync(method!, controller, null);

        actionResult.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Execute_ValidCommand_ReturnsOkWithResult()
    {
        var controller = CreateController();
        var method = controller.GetType().GetMethod("Execute", BindingFlags.Instance | BindingFlags.Public);
        var request = CreateExecuteRequest(method!, "/help");

        var actionResult = await InvokeAsActionResultAsync(method!, controller, [request, CancellationToken.None]);

        actionResult.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Execute_EmptyInput_ReturnsBadRequest()
    {
        var controller = CreateController();
        var method = controller.GetType().GetMethod("Execute", BindingFlags.Instance | BindingFlags.Public);
        var request = CreateExecuteRequest(method!, string.Empty);

        var actionResult = await InvokeAsActionResultAsync(method!, controller, [request, CancellationToken.None]);

        actionResult.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Execute_UnknownCommand_ReturnsNotFound()
    {
        var controller = CreateController();
        var method = controller.GetType().GetMethod("Execute", BindingFlags.Instance | BindingFlags.Public);
        var request = CreateExecuteRequest(method!, "/unknown");

        var actionResult = await InvokeAsActionResultAsync(method!, controller, [request, CancellationToken.None]);

        var notFound = actionResult is NotFoundObjectResult or NotFoundResult;
        notFound.Should().BeTrue();
    }

    [Fact]
    public async Task Execute_NullInput_ReturnsBadRequest()
    {
        var controller = CreateController();
        var method = controller.GetType().GetMethod("Execute", BindingFlags.Instance | BindingFlags.Public);
        var request = CreateExecuteRequest(method!, null);

        var actionResult = await InvokeAsActionResultAsync(method!, controller, [request, CancellationToken.None]);

        actionResult.Should().BeOfType<BadRequestObjectResult>();
    }

    private static object CreateController()
    {
        var controllerType = ResolveType(
            "BotNexus.Gateway.Api.Controllers.CommandsController, BotNexus.Gateway.Api");

        var commandRegistry = new CommandRegistry(
        [
            new StubContributor()
        ], NullLogger<CommandRegistry>.Instance);

        var supervisor = new Mock<IAgentSupervisor>();
        var home = new BotNexusHome(@"Q:\repos\botnexus\.botnexus");

        return CreateInstance(controllerType, new Dictionary<Type, object>
        {
            [typeof(CommandRegistry)] = commandRegistry,
            [typeof(IAgentSupervisor)] = supervisor.Object,
            [typeof(BotNexusHome)] = home
        });
    }

    private static object CreateExecuteRequest(MethodInfo executeMethod, string? input)
    {
        var requestType = executeMethod.GetParameters().First().ParameterType;
        var request = Activator.CreateInstance(requestType)
                      ?? throw new InvalidOperationException($"Could not create {requestType.Name}.");

        SetPropertyIfPresent(requestType, request, "Input", input);
        SetPropertyIfPresent(requestType, request, "AgentId", "nova");
        SetPropertyIfPresent(requestType, request, "SessionId", "session-1");
        return request;
    }

    private static void SetPropertyIfPresent(Type type, object instance, string propertyName, object? value)
    {
        var property = type.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        if (property?.CanWrite == true)
            property.SetValue(instance, value);
    }

    private static async Task<IActionResult> InvokeAsActionResultAsync(MethodInfo method, object instance, object?[]? args)
    {
        object? invocationResult;
        try
        {
            invocationResult = method.Invoke(instance, args);
        }
        catch (TargetInvocationException ex)
        {
            throw ex.InnerException ?? ex;
        }

        if (invocationResult is Task task)
        {
            await task.ConfigureAwait(false);
            invocationResult = task.GetType().GetProperty("Result", BindingFlags.Public | BindingFlags.Instance)?.GetValue(task);
        }

        if (invocationResult is IActionResult actionResult)
            return actionResult;

        var resultProperty = invocationResult?.GetType().GetProperty("Result", BindingFlags.Public | BindingFlags.Instance);
        return (IActionResult)(resultProperty?.GetValue(invocationResult)
            ?? throw new InvalidOperationException($"Could not extract IActionResult from {method.Name}."));
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

    private sealed class StubContributor : ICommandContributor
    {
        private static readonly IReadOnlyList<CommandDescriptor> Commands =
        [
            new()
            {
                Name = "/help",
                Description = "Show available commands",
                Category = "System"
            }
        ];

        public IReadOnlyList<CommandDescriptor> GetCommands() => Commands;

        public Task<CommandResult> ExecuteAsync(
            string commandName,
            CommandExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            if (string.Equals(commandName, "/help", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new CommandResult
                {
                    Title = "Commands",
                    Body = "/help",
                    IsError = false
                });
            }

            return Task.FromResult(new CommandResult
            {
                Title = "Not Found",
                Body = $"Unknown command: {commandName}",
                IsError = true
            });
        }
    }
}
