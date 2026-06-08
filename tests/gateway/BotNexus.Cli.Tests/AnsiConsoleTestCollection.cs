namespace BotNexus.Cli.Tests;

/// <summary>
/// Serializes all test classes that mutate the static <see cref="Spectre.Console.AnsiConsole.Console"/>
/// property. Without this collection, xUnit runs test classes in parallel and the shared
/// writer can be swapped mid-assertion — producing empty or interleaved output.
/// </summary>
[CollectionDefinition("AnsiConsole")]
public sealed class AnsiConsoleTestCollection;
