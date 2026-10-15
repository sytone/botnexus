using System.IO.Abstractions;
using BotNexus.Agent.Core.Types;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using NSubstitute;

namespace BotNexus.Extensions.Skills.Tests;

public sealed class SkillsToolContributorStartupTests
{
    [Fact]
    public async Task ContributeAsync_ConcurrentIndependentContributors_SeedCompletePromptAndReturnAliases()
    {
        using var home = new IsolatedHome();
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callers = Enumerable.Range(0, 16).Select(index => Task.Run(async () =>
        {
            await start.Task;
            var contributor = new SkillsToolContributor(home.Path, new FileSystem());
            return await contributor.ContributeAsync(Context(home.Path, index));
        })).ToArray();
        start.SetResult(true);

        var contributions = await Task.WhenAll(callers).WaitAsync(TimeSpan.FromSeconds(30));

        contributions.ShouldAllBe(contribution => contribution.Tools.Count == 3);
        foreach (var contribution in contributions)
            contribution.Tools.Select(tool => tool.Name).ShouldBe(new[] { "skills", "skills_list", "skill_view" });
        using var stream = typeof(SkillsPromptSeeder).Assembly.GetManifestResourceStream(
            "BotNexus.Extensions.Skills.Resources.Prompts.optimize-shared-skills.prompt.md")
            ?? throw new InvalidOperationException("Bundled prompt resource missing.");
        using var reader = new StreamReader(stream);
        var prompts = System.IO.Path.Combine(home.Path, ".botnexus", "prompts");
        var destination = System.IO.Path.Combine(prompts, "optimize-shared-skills.prompt.md");
        File.ReadAllText(destination).ShouldBe(reader.ReadToEnd());
        Directory.GetFiles(prompts).ShouldBe(new[] { destination });
        File.ReadAllText(home.ExampleSkillPath).ShouldBe("preseeded operator skill\n");
    }

    [Fact]
    public void ContributeAsync_CanceledBeforeStartup_DoesNotSeedPrompt()
    {
        using var home = new IsolatedHome();
        var contributor = new SkillsToolContributor(home.Path, new FileSystem());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Should.Throw<OperationCanceledException>(() =>
            contributor.ContributeAsync(Context(home.Path, 0), cancellation.Token));

        Directory.Exists(System.IO.Path.Combine(home.Path, ".botnexus", "prompts")).ShouldBeFalse();
    }

    [Fact]
    public void ContributeAsync_PermanentPublicationFailure_PropagatesAndCleansOwnedTemp()
    {
        using var home = new IsolatedHome();
        var real = new FileSystem();
        var file = Substitute.ForPartsOf<FileWrapper>(real);
        var fs = Substitute.For<IFileSystem>();
        fs.Path.Returns(real.Path);
        fs.Directory.Returns(real.Directory);
        fs.File.Returns(file);
        var failure = new IOException("injected contributor publication failure", 1);
        var attempts = 0;
        file.When(x => x.Move(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())).DoNotCallBase();
        file.When(x => x.Move(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())).Do(call =>
        {
            attempts++;
            var source = call.ArgAt<string>(0);
            var destination = call.ArgAt<string>(1);
            real.File.ReadAllText(source).ShouldContain("optimize");
            real.File.Exists(destination).ShouldBeFalse();
            call.ArgAt<bool>(2).ShouldBeFalse();
            throw failure;
        });
        var contributor = new SkillsToolContributor(home.Path, fs);

        var error = Should.Throw<IOException>(() => contributor.ContributeAsync(Context(home.Path, 0)));

        error.ShouldBeSameAs(failure);
        attempts.ShouldBe(1);
        Directory.GetFiles(System.IO.Path.Combine(home.Path, ".botnexus", "prompts")).ShouldBeEmpty();
        File.ReadAllText(home.ExampleSkillPath).ShouldBe("preseeded operator skill\n");
    }

    [Fact]
    public async Task ContributeAsync_EmptyHomeConcurrentContributors_SeedCompleteExampleAndPrompt()
    {
        using var home = new IsolatedHome(seedExample: false);
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callers = Enumerable.Range(0, 16).Select(index => Task.Run(async () =>
        {
            await start.Task;
            return await new SkillsToolContributor(home.Path, new FileSystem()).ContributeAsync(Context(home.Path, index));
        })).ToArray();
        start.SetResult(true);

        var contributions = await Task.WhenAll(callers).WaitAsync(TimeSpan.FromSeconds(30));

        foreach (var contribution in contributions)
            contribution.Tools.Select(tool => tool.Name).ShouldBe(new[] { "skills", "skills_list", "skill_view" });
        File.ReadAllText(home.ExampleSkillPath).ShouldBe(SkillsSeederConcurrencyTests.ExpectedContent());
        Directory.GetFiles(System.IO.Path.GetDirectoryName(home.ExampleSkillPath)
            ?? throw new InvalidOperationException("Example parent missing.")).ShouldBe(new[] { home.ExampleSkillPath });
        var prompts = System.IO.Path.Combine(home.Path, ".botnexus", "prompts");
        var prompt = System.IO.Path.Combine(prompts, "optimize-shared-skills.prompt.md");
        File.ReadAllText(prompt).ShouldContain("name: optimize-shared-skills");
        Directory.GetFiles(prompts).ShouldBe(new[] { prompt });
    }

    [Fact]
    public async Task ContributeAsync_OtherExistingSkill_DoesNotCreateExample()
    {
        using var home = new IsolatedHome(seedExample: false);
        var existing = System.IO.Path.Combine(home.Path, ".botnexus", "skills", "operator", "SKILL.md");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(existing)
            ?? throw new InvalidOperationException("Operator parent missing."));
        const string content = "---\nname: operator\ndescription: operator-owned skill\n---\n";
        File.WriteAllText(existing, content);

        var contribution = await new SkillsToolContributor(home.Path, new FileSystem()).ContributeAsync(Context(home.Path, 0));

        contribution.Tools.Count.ShouldBe(3);
        File.Exists(home.ExampleSkillPath).ShouldBeFalse();
        File.ReadAllText(existing).ShouldBe(content);
    }

    [Fact]
    public void ContributeAsync_ExamplePublicationFailure_PropagatesOriginalAndDoesNotSeedPrompt()
    {
        using var home = new IsolatedHome(seedExample: false);
        var real = new FileSystem();
        var file = Substitute.ForPartsOf<FileWrapper>(real);
        var fs = Substitute.For<IFileSystem>();
        fs.Path.Returns(real.Path);
        fs.Directory.Returns(real.Directory);
        fs.File.Returns(file);
        var failure = new UnauthorizedAccessException("injected example publication failure");
        var attempts = 0;
        file.When(x => x.Move(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())).DoNotCallBase();
        file.When(x => x.Move(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())).Do(call =>
        {
            attempts++;
            real.File.ReadAllText(call.ArgAt<string>(0)).ShouldBe(SkillsSeederConcurrencyTests.ExpectedContent());
            call.ArgAt<string>(1).ShouldBe(home.ExampleSkillPath);
            call.ArgAt<bool>(2).ShouldBeFalse();
            throw failure;
        });

        var error = Should.Throw<UnauthorizedAccessException>(() =>
            new SkillsToolContributor(home.Path, fs).ContributeAsync(Context(home.Path, 0)));

        error.ShouldBeSameAs(failure);
        attempts.ShouldBe(1);
        File.Exists(home.ExampleSkillPath).ShouldBeFalse();
        Directory.GetFiles(System.IO.Path.GetDirectoryName(home.ExampleSkillPath)
            ?? throw new InvalidOperationException("Example parent missing.")).ShouldBeEmpty();
        Directory.Exists(System.IO.Path.Combine(home.Path, ".botnexus", "prompts")).ShouldBeFalse();
    }

    private static AgentToolContributionContext Context(string home, int index) => new(
        new AgentDescriptor
        {
            AgentId = AgentId.From($"seed-test-{index}"),
            DisplayName = "seed-test",
            ModelId = "test-model",
            ApiProvider = "test-provider"
        },
        new AgentExecutionContext { SessionId = SessionId.Create() },
        System.IO.Path.Combine(home, "workspace", index.ToString()),
        Substitute.For<IPathValidator>(),
        null,
        (_, _) => Task.FromResult<string?>(null));

    private sealed class IsolatedHome : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "botnexus-4861-startup-" + Guid.NewGuid().ToString("N"));
        public string ExampleSkillPath => System.IO.Path.Combine(Path, ".botnexus", "skills", "example-skill", "SKILL.md");

        public IsolatedHome(bool seedExample = true)
        {
            Directory.CreateDirectory(Path);
            if (seedExample)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ExampleSkillPath)
                    ?? throw new InvalidOperationException("Example skill parent missing."));
                File.WriteAllText(ExampleSkillPath, "preseeded operator skill\n");
            }
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
