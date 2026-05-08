using BotNexus.Gateway.Abstractions.Extensions;
using BotNexus.Gateway.Commands;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests.Commands;

public sealed class CommandRegistryTests
{
    [Fact]
    public void GetAll_NoContributors_ReturnsEmptyList()
    {
        var registry = CreateRegistry([]);

        var commands = registry.GetAll();

        commands.ShouldBeEmpty();
    }

    [Fact]
    public void GetAll_SingleContributor_ReturnsItsCommands()
    {
        var contributor = new StubContributor(
            descriptors:
            [
                Descriptor("/skills", "Skills command")
            ]);
        var registry = CreateRegistry([contributor]);

        var commands = registry.GetAll();

        commands.Where(c => c.Name == "/skills").ShouldHaveSingleItem();
    }

    [Fact]
    public void GetAll_MultipleContributors_ReturnsCombinedCommands()
    {
        var first = new StubContributor([Descriptor("/skills", "Skills command")]);
        var second = new StubContributor([Descriptor("/mcp", "Mcp command")]);
        var registry = CreateRegistry([first, second]);

        var commandNames = registry.GetAll().Select(c => c.Name);

        commandNames.ToList().ShouldBe(new[] { "/skills", "/mcp" }, ignoreOrder: false);
    }

    [Fact]
    public async Task ExecuteAsync_KnownCommand_DelegatesToContributor()
    {
        var contributor = new StubContributor(
            descriptors:
            [
                Descriptor("/skills", "Skills command")
            ],
            execute: (_, _, _) => Task.FromResult(Success("ok")));
        var registry = CreateRegistry([contributor]);

        await registry.ExecuteAsync("/skills", CreateContext("/skills"), CancellationToken.None);

        contributor.LastCommandName.ShouldBe("/skills");
    }

    [Fact]
    public async Task ExecuteAsync_UnknownCommand_ReturnsErrorResult()
    {
        var contributor = new StubContributor([Descriptor("/skills", "Skills command")]);
        var registry = CreateRegistry([contributor]);

        var result = await registry.ExecuteAsync("/unknown", CreateContext("/unknown"), CancellationToken.None);

        result.IsError.ShouldBeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_WithSubCommand_ParsesCorrectly()
    {
        var contributor = new StubContributor(
            descriptors:
            [
                Descriptor("/skills", "Skills command")
            ],
            execute: (_, _, _) => Task.FromResult(Success("ok")));
        var registry = CreateRegistry([contributor]);

        await registry.ExecuteAsync("/skills list", CreateContext("/skills list"), CancellationToken.None);

        contributor.LastContext!.SubCommand.ShouldBe("list");
    }

    [Fact]
    public async Task ExecuteAsync_WithArguments_ParsesCorrectly()
    {
        var contributor = new StubContributor(
            descriptors:
            [
                Descriptor("/skills", "Skills command")
            ],
            execute: (_, _, _) => Task.FromResult(Success("ok")));
        var registry = CreateRegistry([contributor]);

        await registry.ExecuteAsync(
            "/skills info ado-work-management",
            CreateContext("/skills info ado-work-management"),
            CancellationToken.None);

        contributor.LastContext!.Arguments.ShouldHaveSingleItem().ShouldBe("ado-work-management");
    }

    [Fact]
    public async Task GetAll_DuplicateCommandName_FirstRegisteredWins()
    {
        var first = new StubContributor(
            [Descriptor("/skills", "First")],
            execute: (_, _, _) => Task.FromResult(Success("first")));
        var second = new StubContributor(
            [Descriptor("/skills", "Second")],
            execute: (_, _, _) => Task.FromResult(Success("second")));
        var registry = CreateRegistry([first, second]);

        var result = await registry.ExecuteAsync("/skills", CreateContext("/skills"), CancellationToken.None);

        result.Body.ShouldBe("first");
    }

    [Fact]
    public async Task ExecuteAsync_EmptyInput_ReturnsErrorResult()
    {
        var contributor = new StubContributor([Descriptor("/skills", "Skills command")]);
        var registry = CreateRegistry([contributor]);

        var result = await registry.ExecuteAsync(string.Empty, CreateContext(string.Empty), CancellationToken.None);

        result.IsError.ShouldBeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_ContributorThrows_ReturnsErrorResult()
    {
        var contributor = new StubContributor(
            descriptors:
            [
                Descriptor("/skills", "Skills command")
            ],
            execute: (_, _, _) => throw new InvalidOperationException("boom"));
        var registry = CreateRegistry([contributor]);

        var result = await registry.ExecuteAsync("/skills", CreateContext("/skills"), CancellationToken.None);

        result.IsError.ShouldBeTrue();
    }

    private static CommandRegistry CreateRegistry(IEnumerable<ICommandContributor> contributors)
        => new(contributors, NullLogger<CommandRegistry>.Instance);

    private static CommandExecutionContext CreateContext(string rawInput)
        => new()
        {
            RawInput = rawInput,
            HomeDirectory = @"Q:\repos\botnexus"
        };

    private static CommandDescriptor Descriptor(string name, string description)
        => new()
        {
            Name = name,
            Description = description
        };

    private static CommandResult Success(string body)
        => new()
        {
            Title = "ok",
            Body = body,
            IsError = false
        };

    private sealed class StubContributor(
        IReadOnlyList<CommandDescriptor> descriptors,
        Func<string, CommandExecutionContext, CancellationToken, Task<CommandResult>>? execute = null)
        : ICommandContributor
    {
        public string? LastCommandName { get; private set; }

        public CommandExecutionContext? LastContext { get; private set; }

        public IReadOnlyList<CommandDescriptor> GetCommands() => descriptors;

        public Task<CommandResult> ExecuteAsync(
            string commandName,
            CommandExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            LastCommandName = commandName;
            LastContext = context;
            return execute?.Invoke(commandName, context, cancellationToken)
                   ?? Task.FromResult(Success("default"));
        }
    }
}
