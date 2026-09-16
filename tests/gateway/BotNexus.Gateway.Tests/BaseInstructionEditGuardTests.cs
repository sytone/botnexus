using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Isolation;
using BotNexus.Gateway.Prompts;
using BotNexus.Tools;

namespace BotNexus.Gateway.Tests;

public sealed class BaseInstructionEditGuardTests
{
    [Theory]
    [InlineData("write")]
    [InlineData("edit")]
    public void Evaluate_BaseInstructionFileWithoutClassification_ReturnsClassificationPrompt(string toolName)
    {
        var guard = CreateGuard();

        var prompt = guard.Evaluate(toolName, Arguments("AGENTS.md"));

        prompt.ShouldNotBeNull();
        prompt.ShouldContain("agnostic");
        prompt.ShouldContain("model-specific");
        prompt.ShouldContain("model_profile");
        prompt.ShouldContain("instructionScope");
        prompt.ShouldContain(ContextFileVariants.GrammarPattern);
    }

    [Theory]
    [InlineData("agnostic")]
    [InlineData("model-specific")]
    public void Evaluate_BaseInstructionFileWithClassification_AllowsMutation(string classification)
    {
        var guard = CreateGuard();
        var arguments = Arguments("AGENTS.md", classification);

        guard.Evaluate("edit", arguments).ShouldBeNull();
    }

    [Fact]
    public void Evaluate_ModelVariant_DoesNotTriggerAndUsesContextFileVariantsResolver()
    {
        var calls = 0;
        string Resolver(string fileName)
        {
            calls++;
            return ContextFileVariants.GetBaseFileName(fileName);
        }

        var guard = CreateGuard(Resolver);

        guard.Evaluate("write", Arguments("AGENTS.gpt-5.md")).ShouldBeNull();
        calls.ShouldBe(1);
    }

    [Fact]
    public void Evaluate_ConfiguredCustomBaseFile_TriggersButSameStemElsewhereDoesNot()
    {
        var descriptor = Descriptor() with { SystemPromptFiles = ["prompts/reviewer.md"] };
        var guard = new BaseInstructionEditGuard(descriptor, WorkspacePath);

        guard.Evaluate("edit", Arguments("prompts/reviewer.md")).ShouldNotBeNull();
        guard.Evaluate("edit", Arguments("notes/reviewer.md")).ShouldBeNull();
    }

    [Theory]
    [InlineData("read")]
    [InlineData("grep")]
    public void Evaluate_NonMutationTool_DoesNotTrigger(string toolName)
    {
        CreateGuard().Evaluate(toolName, Arguments("AGENTS.md")).ShouldBeNull();
    }

    [Fact]
    public async Task WorkspaceMutationTools_ExposeInstructionScopeClassification()
    {
        var writeDefinition = new WriteTool(WorkspacePath).Definition.Parameters.GetProperty("properties");
        var editDefinition = new EditTool(WorkspacePath).Definition.Parameters.GetProperty("properties");

        writeDefinition.TryGetProperty("instructionScope", out _).ShouldBeTrue();
        editDefinition.TryGetProperty("instructionScope", out _).ShouldBeTrue();

        var writePrepared = await new WriteTool(WorkspacePath).PrepareArgumentsAsync(
            new Dictionary<string, object?>
            {
                ["path"] = "AGENTS.md",
                ["content"] = "content",
                ["instructionScope"] = "agnostic"
            });
        writePrepared["instructionScope"].ShouldBe("agnostic");
    }

    private const string WorkspacePath = "C:/workspace";

    private static BaseInstructionEditGuard CreateGuard(Func<string, string>? resolver = null) =>
        new(Descriptor(), WorkspacePath, resolver);

    private static AgentDescriptor Descriptor() => new()
    {
        AgentId = AgentId.From("agent-a"),
        DisplayName = "Agent A",
        ModelId = "gpt-5",
        ApiProvider = "test-provider",
        SystemPrompt = "prompt"
    };

    private static IReadOnlyDictionary<string, object?> Arguments(string path, string? classification = null)
    {
        var arguments = new Dictionary<string, object?> { ["path"] = path };
        if (classification is not null)
            arguments["instructionScope"] = classification;
        return arguments;
    }
}
