using System.Reflection;
using BotNexus.CodingAgent.Extensions;

namespace BotNexus.CodingAgent.Tests;

public sealed class ProgramTests
{
    private static Type ProgramType => typeof(SkillsLoader).Assembly.GetType("BotNexus.CodingAgent.Program", throwOnError: true)!;

    [Fact]
    public void CombinePrompt_WhenStdinAndCliProvided_ConcatenatesInOrder()
    {
        var method = ProgramType.GetMethod("CombinePrompt", BindingFlags.NonPublic | BindingFlags.Static);
        method.ShouldNotBeNull();

        var combined = (string?)method!.Invoke(null, ["from-stdin ", "from-cli"]);

        combined.ShouldBe("from-stdin from-cli");
    }

    [Fact]
    public async Task ReadPipedStdinAsync_WhenInputIsNotRedirected_ReturnsNull()
    {
        if (Console.IsInputRedirected)
        {
            return;
        }

        var method = ProgramType.GetMethod("ReadPipedStdinAsync", BindingFlags.NonPublic | BindingFlags.Static);
        method.ShouldNotBeNull();

        var task = (Task<string?>)method!.Invoke(null, null)!;
        var result = await task;
        result.ShouldBeNull();
    }
}
