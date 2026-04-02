using System.CommandLine;
using BotNexus.Cli.Services;

var homeOption = new Option<string?>("--home")
{
    Description = "Override BOTNEXUS_HOME for this command."
};
homeOption.Recursive = true;

var rootCommand = new RootCommand("BotNexus CLI");
rootCommand.Add(homeOption);

rootCommand.Add(CreatePlaceholderCommand("config", homeOption));
rootCommand.Add(CreatePlaceholderCommand("agent", homeOption));
rootCommand.Add(CreatePlaceholderCommand("provider", homeOption));
rootCommand.Add(CreatePlaceholderCommand("channel", homeOption));
rootCommand.Add(CreatePlaceholderCommand("extension", homeOption));
rootCommand.Add(CreatePlaceholderCommand("doctor", homeOption));
rootCommand.Add(CreatePlaceholderCommand("status", homeOption));
rootCommand.Add(CreatePlaceholderCommand("logs", homeOption));
rootCommand.Add(CreatePlaceholderCommand("start", homeOption));
rootCommand.Add(CreatePlaceholderCommand("stop", homeOption));
rootCommand.Add(CreatePlaceholderCommand("restart", homeOption));

return rootCommand.Parse(args).Invoke();

static Command CreatePlaceholderCommand(string name, Option<string?> homeOption)
{
    var command = new Command(name, $"{name} command");
    command.SetAction(parseResult =>
    {
        var homePath = parseResult.GetValue(homeOption);
        if (!string.IsNullOrWhiteSpace(homePath))
            Environment.SetEnvironmentVariable("BOTNEXUS_HOME", homePath);

        ConsoleOutput.WriteStatus(ConsoleStatus.Warning, "Not implemented yet");
    });

    return command;
}
