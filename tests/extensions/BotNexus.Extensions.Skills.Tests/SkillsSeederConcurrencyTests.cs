using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using System.Text;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.Extensions;

namespace BotNexus.Extensions.Skills.Tests;

public sealed class SkillsSeederConcurrencyTests
{
    private const string OperatorContent = "operator customization — preserve exactly\n";

    [Fact]
    public void EnsureGlobalSkillsSeed_OperatorCreatesAfterAbsentCheck_PreservesOperatorContent()
    {
        using var fixture = new ForwardingFileSystem();
        var checks = 0;
        fixture.File.Configure().Exists(fixture.Destination).Returns(_ =>
        {
            var existed = fixture.Real.File.Exists(fixture.Destination);
            // The first check is skill discovery; the second is the destination fast path.
            if (++checks == 2)
                fixture.Real.File.WriteAllText(fixture.Destination, OperatorContent);
            return existed;
        });

        SkillsSeeder.EnsureGlobalSkillsSeed(fixture.Root, fixture.FileSystem);

        checks.ShouldBeGreaterThanOrEqualTo(2);
        fixture.Real.File.ReadAllText(fixture.Destination).ShouldBe(OperatorContent);
        fixture.AssertOnlyDestinationRemains();
    }

    [Fact]
    public void EnsureGlobalSkillsSeed_OperatorCreatesAtPublication_PreservesOperatorContent()
    {
        using var fixture = new ForwardingFileSystem();
        fixture.BeforeMove = (_, _, _) => fixture.Real.File.WriteAllText(fixture.Destination, OperatorContent);

        SkillsSeeder.EnsureGlobalSkillsSeed(fixture.Root, fixture.FileSystem);

        fixture.MoveAttempts.ShouldBe(1);
        fixture.Real.File.ReadAllText(fixture.Destination).ShouldBe(OperatorContent);
        fixture.AssertOnlyDestinationRemains();
    }

    [Fact]
    public void EnsureGlobalSkillsSeed_WriteInProgress_AnotherSeederPublishesOnlyCompleteContent()
    {
        using var fixture = new ForwardingFileSystem();
        var expected = ExpectedContent();
        var writes = new List<string>();
        fixture.DuringWrite = path =>
        {
            writes.Add(path);
            fixture.Real.File.Exists(fixture.Destination).ShouldBeFalse("a partial seed must never be discoverable");
            SkillsSeeder.EnsureGlobalSkillsSeed(fixture.Root, new FileSystem());
            fixture.Real.File.ReadAllText(fixture.Destination).ShouldBe(expected);
        };
        fixture.BeforeMove = (source, destination, overwrite) =>
        {
            source.ShouldNotBe(destination);
            Path.GetDirectoryName(source).ShouldBe(fixture.ExampleDirectory);
            overwrite.ShouldBeFalse();
            fixture.Real.File.ReadAllText(source).ShouldBe(expected);
            using var closed = fixture.Real.File.Open(source, FileMode.Open, FileAccess.Read, FileShare.None);
        };

        SkillsSeeder.EnsureGlobalSkillsSeed(fixture.Root, fixture.FileSystem);

        writes.ShouldHaveSingleItem().ShouldNotBe(fixture.Destination);
        fixture.MoveAttempts.ShouldBe(1);
        fixture.Real.File.ReadAllText(fixture.Destination).ShouldBe(expected);
        fixture.AssertOnlyDestinationRemains();
    }

    [Fact]
    public void EnsureGlobalSkillsSeed_GlobalDirectoryCreatedByOtherCaller_DoesNotSeedOverExistingSkills()
    {
        using var fixture = new ForwardingFileSystem();
        fixture.Directory.Configure().Exists(fixture.Root).Returns(false);
        var existing = Path.Combine(fixture.Root, "operator-skill", "SKILL.md");
        fixture.Directory.When(x => x.CreateDirectory(fixture.Root)).Do(_ =>
        {
            fixture.Real.Directory.CreateDirectory(Path.GetDirectoryName(existing)
                ?? throw new InvalidOperationException("Parent missing."));
            fixture.Real.File.WriteAllText(existing, OperatorContent);
        });

        SkillsSeeder.EnsureGlobalSkillsSeed(fixture.Root, fixture.FileSystem);

        fixture.Real.File.Exists(fixture.Destination).ShouldBeFalse();
        fixture.Real.File.ReadAllText(existing).ShouldBe(OperatorContent);
        fixture.MoveAttempts.ShouldBe(0);
    }

    [Fact]
    public void EnsureGlobalSkillsSeed_TransientDirectoryWriteAndPublicationFailures_RetriesCompleteSeed()
    {
        using var fixture = new ForwardingFileSystem();
        var directories = 0;
        fixture.Directory.When(x => x.CreateDirectory(fixture.ExampleDirectory)).Do(_ =>
        {
            if (++directories == 1)
                throw SharingViolation();
        });
        var writes = 0;
        fixture.DuringWrite = _ =>
        {
            if (++writes == 1)
                throw SharingViolation();
        };
        fixture.BeforeMove = (_, _, _) =>
        {
            if (fixture.MoveAttempts <= 2)
                throw SharingViolation();
        };

        SkillsSeeder.EnsureGlobalSkillsSeed(fixture.Root, fixture.FileSystem);

        directories.ShouldBe(2);
        writes.ShouldBe(2);
        fixture.MoveAttempts.ShouldBe(3);
        fixture.Real.File.ReadAllText(fixture.Destination).ShouldBe(ExpectedContent());
        fixture.AssertOnlyDestinationRemains();
    }

    [Fact]
    public void EnsureGlobalSkillsSeed_PublicationRetryLimit_PreservesExceptionAndCleansTemp()
    {
        using var fixture = new ForwardingFileSystem();
        var failure = SharingViolation();
        fixture.BeforeMove = (_, _, _) => throw failure;
        var clock = new RetryClock();

        var error = Should.Throw<IOException>(() => SkillsSeeder.EnsureGlobalSkillsSeed(
            fixture.Root, fixture.FileSystem, null, clock, clock.Delay));

        error.ShouldBeSameAs(failure);
        fixture.MoveAttempts.ShouldBe(8);
        clock.Delays.ShouldBe(7);
        fixture.Real.Directory.GetFiles(fixture.ExampleDirectory).ShouldBeEmpty();
    }

    [Fact]
    public void EnsureGlobalSkillsSeed_RetryDeadlineReachedDuringDelay_DoesNotPublishAgain()
    {
        using var fixture = new ForwardingFileSystem();
        var failure = SharingViolation();
        fixture.BeforeMove = (_, _, _) => throw failure;
        var clock = new RetryClock { DelayAdvance = TimeSpan.FromSeconds(1) };

        var error = Should.Throw<IOException>(() => SkillsSeeder.EnsureGlobalSkillsSeed(
            fixture.Root, fixture.FileSystem, null, clock, clock.Delay));

        error.ShouldBeSameAs(failure);
        fixture.MoveAttempts.ShouldBe(1);
        clock.Delays.ShouldBe(1);
        fixture.Real.Directory.GetFiles(fixture.ExampleDirectory).ShouldBeEmpty();
    }

    private sealed class RetryClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public TimeSpan DelayAdvance { get; init; }
        public int Delays { get; private set; }
        public void Delay(TimeSpan duration)
        {
            duration.ShouldBeGreaterThan(TimeSpan.Zero);
            Delays++;
            _timestamp += DelayAdvance.Ticks;
        }
    }

    [Fact]
    public void EnsureGlobalSkillsSeed_UnrelatedIoFailureWithDestinationPresent_IsNotAcceptedAsCollision()
    {
        using var fixture = new ForwardingFileSystem();
        var failure = new IOException("injected permanent failure", unchecked((int)0x8007001D));
        fixture.BeforeMove = (_, _, _) =>
        {
            fixture.Real.File.WriteAllText(fixture.Destination, OperatorContent);
            throw failure;
        };

        var error = Should.Throw<IOException>(() => SkillsSeeder.EnsureGlobalSkillsSeed(fixture.Root, fixture.FileSystem));

        error.ShouldBeSameAs(failure);
        fixture.MoveAttempts.ShouldBe(1);
        fixture.Real.File.ReadAllText(fixture.Destination).ShouldBe(OperatorContent);
        fixture.AssertOnlyDestinationRemains();
    }

    [Fact]
    public void EnsureGlobalSkillsSeed_PermanentPartialWriteFailure_PreservesExceptionAndCleansTemp()
    {
        using var fixture = new ForwardingFileSystem();
        var failure = new UnauthorizedAccessException("injected write failure");
        fixture.DuringWrite = _ => throw failure;

        var error = Should.Throw<UnauthorizedAccessException>(() => SkillsSeeder.EnsureGlobalSkillsSeed(fixture.Root, fixture.FileSystem));

        error.ShouldBeSameAs(failure);
        fixture.MoveAttempts.ShouldBe(0);
        fixture.Real.Directory.GetFiles(fixture.ExampleDirectory).ShouldBeEmpty();
    }

    [Fact]
    public void EnsureGlobalSkillsSeed_CleanupFailure_LogsWithoutReplacingPublicationFailure()
    {
        using var fixture = new ForwardingFileSystem();
        var failure = new UnauthorizedAccessException("injected publication failure");
        fixture.BeforeMove = (_, _, _) => throw failure;
        fixture.File.When(x => x.Delete(Arg.Any<string>())).Do(_ => throw new UnauthorizedAccessException("cleanup failure"));
        var logger = Substitute.For<ILogger>();

        var error = Should.Throw<UnauthorizedAccessException>(() => SkillsSeeder.EnsureGlobalSkillsSeed(fixture.Root, fixture.FileSystem, logger));

        error.ShouldBeSameAs(failure);
        fixture.MoveAttempts.ShouldBe(1);
        fixture.Real.File.Exists(fixture.Destination).ShouldBeFalse();
        fixture.Real.Directory.GetFiles(fixture.ExampleDirectory).ShouldHaveSingleItem();
        logger.ReceivedCalls().ShouldContain(call => call.GetMethodInfo().Name == "Log"
            && Equals(call.GetArguments()[0], LogLevel.Warning));
    }

    [Fact]
    public async Task EnsureGlobalSkillsSeed_ParallelIndependentFileSystems_ProducesOneCompleteSeed()
    {
        for (var round = 0; round < 8; round++)
        {
            using var fixture = new ForwardingFileSystem();
            fixture.Real.Directory.Delete(fixture.Root, recursive: true);
            var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var callers = Enumerable.Range(0, 16).Select(_ => Task.Run(async () =>
            {
                await start.Task;
                SkillsSeeder.EnsureGlobalSkillsSeed(fixture.Root, new FileSystem());
            })).ToArray();
            start.SetResult(true);

            await Task.WhenAll(callers).WaitAsync(TimeSpan.FromSeconds(30));

            fixture.Real.File.ReadAllText(fixture.Destination).ShouldBe(ExpectedContent());
            fixture.AssertOnlyDestinationRemains();
        }
    }

    internal static string ExpectedContent()
    {
        var fs = new MockFileSystem();
        SkillsSeeder.EnsureGlobalSkillsSeed("/expected", fs);
        return fs.File.ReadAllText(Path.Combine("/expected", "example-skill", "SKILL.md"));
    }

    private static IOException SharingViolation() => new("injected sharing violation", unchecked((int)0x80070020));

    private sealed class ForwardingFileSystem : IDisposable
    {
        public FileSystem Real { get; } = new();
        public IFileSystem FileSystem { get; }
        public FileWrapper File { get; }
        public DirectoryWrapper Directory { get; }
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "botnexus-4863-" + Guid.NewGuid().ToString("N"));
        public string ExampleDirectory => Path.Combine(Root, "example-skill");
        public string Destination => Path.Combine(ExampleDirectory, "SKILL.md");
        public Action<string>? DuringWrite { get; set; }
        public Action<string, string, bool>? BeforeMove { get; set; }
        public int MoveAttempts { get; private set; }

        public ForwardingFileSystem()
        {
            Real.Directory.CreateDirectory(ExampleDirectory);
            File = Substitute.ForPartsOf<FileWrapper>(Real);
            Directory = Substitute.ForPartsOf<DirectoryWrapper>(Real);
            FileSystem = Substitute.For<IFileSystem>();
            FileSystem.File.Returns(File);
            FileSystem.Path.Returns(Real.Path);
            FileSystem.Directory.Returns(Directory);
            File.When(x => x.WriteAllText(Arg.Any<string>(), Arg.Any<string>())).DoNotCallBase();
            File.When(x => x.WriteAllText(Arg.Any<string>(), Arg.Any<string>())).Do(call =>
            {
                var path = call.ArgAt<string>(0);
                var content = call.ArgAt<string>(1);
                Real.File.WriteAllText(path, content[..Math.Min(16, content.Length)], Encoding.UTF8);
                DuringWrite?.Invoke(path);
                Real.File.WriteAllText(path, content, Encoding.UTF8);
            });
            File.When(x => x.Move(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())).DoNotCallBase();
            File.When(x => x.Move(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())).Do(call =>
            {
                if (++MoveAttempts > 20)
                    throw new InvalidOperationException("Publication exceeded bounded retry safety limit.");
                var source = call.ArgAt<string>(0);
                var destination = call.ArgAt<string>(1);
                var overwrite = call.ArgAt<bool>(2);
                BeforeMove?.Invoke(source, destination, overwrite);
                overwrite.ShouldBeFalse();
                SkillsPromptSeeder.PublishCompletedFile(Real, source, destination);
            });
        }

        public void AssertOnlyDestinationRemains() => Real.Directory.GetFiles(ExampleDirectory).ShouldBe(new[] { Destination });
        public void Dispose()
        {
            if (Real.Directory.Exists(Root))
                Real.Directory.Delete(Root, recursive: true);
        }
    }
}
