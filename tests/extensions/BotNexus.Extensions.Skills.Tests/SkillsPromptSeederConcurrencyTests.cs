using System.Diagnostics;
using System.IO.Abstractions;
using System.Text;
using BotNexus.Extensions.Skills;
using NSubstitute;
using NSubstitute.Extensions;
using Microsoft.Extensions.Logging;

namespace BotNexus.Extensions.Skills.Tests;

public sealed class SkillsPromptSeederConcurrencyTests
{
    private const string PromptFileName = "optimize-shared-skills.prompt.md";
    private const string OperatorContent = "operator customization — preserve exactly\n";

    [Fact]
    public void EnsureSharedPromptSeed_OperatorCreatesAfterAbsentCheck_PreservesOperatorContent()
    {
        using var fixture = new ForwardingFileSystem();
        var checks = 0;
        fixture.File.Configure().Exists(fixture.Destination).Returns(_ =>
        {
            var existed = fixture.Real.File.Exists(fixture.Destination);
            if (++checks == 1)
                fixture.Real.File.WriteAllText(fixture.Destination, OperatorContent);
            return existed;
        });

        SkillsPromptSeeder.EnsureSharedPromptSeed(fixture.DirectoryPath, fixture.FileSystem);

        checks.ShouldBeGreaterThan(0);
        fixture.Real.File.ReadAllText(fixture.Destination).ShouldBe(OperatorContent);
        fixture.AssertOnlyDestinationRemains();
    }

    [Fact]
    public void EnsureSharedPromptSeed_OperatorCreatesAtPublication_PreservesOperatorContentAndCleansTemp()
    {
        using var fixture = new ForwardingFileSystem();
        fixture.BeforeMove = (_, _, _) =>
            fixture.Real.File.WriteAllText(fixture.Destination, OperatorContent);

        SkillsPromptSeeder.EnsureSharedPromptSeed(fixture.DirectoryPath, fixture.FileSystem);

        fixture.MoveAttempts.ShouldBe(1);
        fixture.Real.File.ReadAllText(fixture.Destination).ShouldBe(OperatorContent);
        fixture.AssertOnlyDestinationRemains();
    }

    [Fact]
    public void EnsureSharedPromptSeed_WriteInProgress_DestinationIsNotVisibleToAnotherSeeder()
    {
        using var fixture = new ForwardingFileSystem();
        var expected = ReadBundledPrompt();
        var observations = new List<string>();
        var writes = new List<string>();
        fixture.DuringWrite = path =>
        {
            writes.Add(path);
            // The forwarding boundary has written only a prefix and has not completed the write.
            // A concurrent caller must not be able to accept this prefix as an installed prompt.
            if (fixture.Real.File.Exists(fixture.Destination))
            {
                SkillsPromptSeeder.EnsureSharedPromptSeed(fixture.DirectoryPath, new FileSystem());
                observations.Add(fixture.Real.File.ReadAllText(fixture.Destination));
            }
        };
        fixture.BeforeMove = (source, destination, overwrite) =>
        {
            source.ShouldNotBe(destination);
            Path.GetDirectoryName(source).ShouldBe(fixture.DirectoryPath);
            overwrite.ShouldBeFalse();
            fixture.Real.File.ReadAllText(source).ShouldBe(expected);
            // Exclusive open verifies the temporary file was closed before publication.
            using var closedFile = fixture.Real.File.Open(source, FileMode.Open, FileAccess.Read, FileShare.None);
        };

        SkillsPromptSeeder.EnsureSharedPromptSeed(fixture.DirectoryPath, fixture.FileSystem);

        observations.ShouldBeEmpty("no partial destination may be visible while the seed is being written");
        writes.ShouldHaveSingleItem().ShouldNotBe(fixture.Destination);
        fixture.MoveAttempts.ShouldBe(1);
        fixture.Real.File.ReadAllText(fixture.Destination).ShouldBe(expected);
        fixture.AssertOnlyDestinationRemains();
    }

    [Fact]
    public void EnsureSharedPromptSeed_TransientPublicationSharingFailures_RetriesAndPublishesCompletePrompt()
    {
        using var fixture = new ForwardingFileSystem();
        fixture.BeforeMove = (_, _, _) =>
        {
            if (fixture.MoveAttempts <= 2)
                throw SharingViolation();
        };

        SkillsPromptSeeder.EnsureSharedPromptSeed(fixture.DirectoryPath, fixture.FileSystem);

        fixture.MoveAttempts.ShouldBe(3);
        fixture.Real.File.ReadAllText(fixture.Destination).ShouldBe(ReadBundledPrompt());
        fixture.AssertOnlyDestinationRemains();
    }

    [Fact]
    public void EnsureSharedPromptSeed_PublicationSharingFailuresExhausted_ThrowsWithinBoundAndCleansTemp()
    {
        using var fixture = new ForwardingFileSystem();
        fixture.BeforeMove = (_, _, _) => throw SharingViolation();
        var elapsed = Stopwatch.StartNew();

        var error = Should.Throw<IOException>(() =>
            SkillsPromptSeeder.EnsureSharedPromptSeed(fixture.DirectoryPath, fixture.FileSystem));

        elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
        fixture.MoveAttempts.ShouldBeInRange(2, 20);
        error.ToString().ShouldContain("injected publication sharing violation");
        fixture.Real.File.Exists(fixture.Destination).ShouldBeFalse();
        fixture.Real.Directory.GetFiles(fixture.DirectoryPath).ShouldBeEmpty();
    }

    [Fact]
    public void EnsureSharedPromptSeed_PermanentPublicationFailure_ThrowsWithoutRetryAndCleansTemp()
    {
        using var fixture = new ForwardingFileSystem();
        var denied = new UnauthorizedAccessException("injected publication access denied");
        fixture.BeforeMove = (_, _, _) => throw denied;

        var error = Should.Throw<UnauthorizedAccessException>(() =>
            SkillsPromptSeeder.EnsureSharedPromptSeed(fixture.DirectoryPath, fixture.FileSystem));

        fixture.MoveAttempts.ShouldBe(1);
        error.ShouldBeSameAs(denied);
        fixture.Real.File.Exists(fixture.Destination).ShouldBeFalse();
        fixture.Real.Directory.GetFiles(fixture.DirectoryPath).ShouldBeEmpty();
    }

    [Fact]
    public async Task EnsureSharedPromptSeed_ParallelIndependentFileSystems_ProducesOneCompletePromptWithoutExceptions()
    {
        var expected = ReadBundledPrompt();
        for (var round = 0; round < 8; round++)
        {
            using var fixture = new ForwardingFileSystem();
            var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var callers = Enumerable.Range(0, 16).Select(_ => Task.Run(async () =>
            {
                await start.Task;
                // Independent filesystem objects model callers without a shared coordination object.
                SkillsPromptSeeder.EnsureSharedPromptSeed(fixture.DirectoryPath, new FileSystem());
            })).ToArray();
            start.SetResult(true);

            await Task.WhenAll(callers).WaitAsync(TimeSpan.FromSeconds(30));

            fixture.Real.File.ReadAllText(fixture.Destination).ShouldBe(expected);
            fixture.AssertOnlyDestinationRemains();
        }
    }

    [Fact]
    public void EnsureSharedPromptSeed_TransientDirectoryAndPartialWriteFailures_RetriesBeforePublication()
    {
        using var fixture = new ForwardingFileSystem();
        var directories = 0;
        fixture.Directory.When(x => x.CreateDirectory(fixture.DirectoryPath)).Do(_ =>
        {
            if (++directories <= 2)
                throw SharingViolation();
        });
        var writes = 0;
        fixture.DuringWrite = _ =>
        {
            if (++writes <= 2)
                throw SharingViolation();
        };
        var clock = new RetryClock();

        SkillsPromptSeeder.EnsureSharedPromptSeed(fixture.DirectoryPath, fixture.FileSystem, null, clock, clock.Delay);

        directories.ShouldBe(3);
        writes.ShouldBe(3);
        clock.Delays.ShouldBe(4);
        fixture.MoveAttempts.ShouldBe(1);
        fixture.Real.File.ReadAllText(fixture.Destination).ShouldBe(ReadBundledPrompt());
        fixture.AssertOnlyDestinationRemains();
    }

    [Fact]
    public void EnsureSharedPromptSeed_RetryAttemptLimit_PreservesLastExceptionAndCleansTemp()
    {
        using var fixture = new ForwardingFileSystem();
        var failure = SharingViolation();
        fixture.BeforeMove = (_, _, _) => throw failure;
        var clock = new RetryClock { AdvanceOnDelay = false };

        var error = Should.Throw<IOException>(() => SkillsPromptSeeder.EnsureSharedPromptSeed(
            fixture.DirectoryPath, fixture.FileSystem, null, clock, clock.Delay));

        error.ShouldBeSameAs(failure);
        fixture.MoveAttempts.ShouldBe(8);
        clock.Delays.ShouldBe(7);
        fixture.Real.Directory.GetFiles(fixture.DirectoryPath).ShouldBeEmpty();
    }

    [Fact]
    public void EnsureSharedPromptSeed_RetryDeadlineReachedDuringDelay_DoesNotAttemptPublicationAgain()
    {
        using var fixture = new ForwardingFileSystem();
        var failure = SharingViolation();
        fixture.BeforeMove = (_, _, _) => throw failure;
        var clock = new RetryClock { DelayAdvance = TimeSpan.FromSeconds(1) };

        var error = Should.Throw<IOException>(() => SkillsPromptSeeder.EnsureSharedPromptSeed(
            fixture.DirectoryPath, fixture.FileSystem, null, clock, clock.Delay));

        error.ShouldBeSameAs(failure);
        fixture.MoveAttempts.ShouldBe(1);
        clock.Delays.ShouldBe(1);
        fixture.Real.Directory.GetFiles(fixture.DirectoryPath).ShouldBeEmpty();
    }

    [Fact]
    public void EnsureSharedPromptSeed_UnrelatedIoFailureWithDestinationPresent_IsNotAcceptedAsCollision()
    {
        using var fixture = new ForwardingFileSystem();
        var failure = new IOException("injected permanent I/O failure", unchecked((int)0x8007001D));
        fixture.BeforeMove = (_, _, _) =>
        {
            fixture.Real.File.WriteAllText(fixture.Destination, OperatorContent);
            throw failure;
        };

        var error = Should.Throw<IOException>(() =>
            SkillsPromptSeeder.EnsureSharedPromptSeed(fixture.DirectoryPath, fixture.FileSystem));

        error.ShouldBeSameAs(failure);
        fixture.MoveAttempts.ShouldBe(1);
        fixture.Real.File.ReadAllText(fixture.Destination).ShouldBe(OperatorContent);
        fixture.AssertOnlyDestinationRemains();
    }

    [Fact]
    public void EnsureSharedPromptSeed_PermanentPartialWriteFailure_PreservesExceptionAndCleansTemp()
    {
        using var fixture = new ForwardingFileSystem();
        var failure = new IOException("injected permanent write failure", unchecked((int)0x8007001D));
        fixture.DuringWrite = _ => throw failure;

        var error = Should.Throw<IOException>(() =>
            SkillsPromptSeeder.EnsureSharedPromptSeed(fixture.DirectoryPath, fixture.FileSystem));

        error.ShouldBeSameAs(failure);
        fixture.MoveAttempts.ShouldBe(0);
        fixture.Real.Directory.GetFiles(fixture.DirectoryPath).ShouldBeEmpty();
    }

    [Fact]
    public void EnsureSharedPromptSeed_TransientCleanupFailure_RetriesOwnedTempDeletion()
    {
        using var fixture = new ForwardingFileSystem();
        fixture.BeforeMove = (_, _, _) => fixture.Real.File.WriteAllText(fixture.Destination, OperatorContent);
        var deletes = 0;
        fixture.File.When(x => x.Delete(Arg.Any<string>())).Do(_ =>
        {
            if (++deletes == 1)
                throw SharingViolation();
        });
        var clock = new RetryClock();

        SkillsPromptSeeder.EnsureSharedPromptSeed(fixture.DirectoryPath, fixture.FileSystem, null, clock, clock.Delay);

        deletes.ShouldBe(2);
        clock.Delays.ShouldBe(1);
        fixture.AssertOnlyDestinationRemains();
        fixture.Real.File.ReadAllText(fixture.Destination).ShouldBe(OperatorContent);
    }

    [Fact]
    public void EnsureSharedPromptSeed_CleanupFailure_LogsWithoutReplacingPublicationFailure()
    {
        using var fixture = new ForwardingFileSystem();
        var failure = new UnauthorizedAccessException("injected publication failure");
        fixture.BeforeMove = (_, _, _) => throw failure;
        fixture.File.When(x => x.Delete(Arg.Any<string>())).Do(_ =>
            throw new UnauthorizedAccessException("injected cleanup failure"));
        var logger = Substitute.For<ILogger>();

        var error = Should.Throw<UnauthorizedAccessException>(() =>
            SkillsPromptSeeder.EnsureSharedPromptSeed(fixture.DirectoryPath, fixture.FileSystem, logger));

        error.ShouldBeSameAs(failure);
        fixture.Real.File.Exists(fixture.Destination).ShouldBeFalse();
        fixture.Real.Directory.GetFiles(fixture.DirectoryPath).ShouldHaveSingleItem();
        logger.ReceivedCalls().ShouldContain(call => call.GetMethodInfo().Name == "Log"
            && Equals(call.GetArguments()[0], LogLevel.Warning));
    }

    [Fact]
    public void PublishCompletedFile_ExistingOperatorFile_PreservesBothFiles()
    {
        using var fixture = new ForwardingFileSystem();
        var source = Path.Combine(fixture.DirectoryPath, "completed.tmp");
        fixture.Real.File.WriteAllText(source, ReadBundledPrompt());
        fixture.Real.File.WriteAllText(fixture.Destination, OperatorContent);

        Should.Throw<IOException>(() => SkillsPromptSeeder.PublishCompletedFile(
            fixture.Real, source, fixture.Destination));

        fixture.Real.File.ReadAllText(fixture.Destination).ShouldBe(OperatorContent);
        fixture.Real.File.ReadAllText(source).ShouldBe(ReadBundledPrompt());
    }

    [Fact]
    public void PublishCompletedFile_RealFilesystem_PublishesCompleteBytesAndUnixSourceRemainsForCleanup()
    {
        using var fixture = new ForwardingFileSystem();
        var source = Path.Combine(fixture.DirectoryPath, "completed-ünicode.tmp");
        fixture.Real.File.WriteAllText(source, ReadBundledPrompt());
        var expected = fixture.Real.File.ReadAllBytes(source);

        SkillsPromptSeeder.PublishCompletedFile(fixture.Real, source, fixture.Destination);

        fixture.Real.File.ReadAllBytes(fixture.Destination).ShouldBe(expected);
        if (!OperatingSystem.IsWindows())
        {
            fixture.Real.File.ReadAllBytes(source).ShouldBe(expected);
            fixture.Real.File.Delete(source);
            fixture.Real.File.ReadAllBytes(fixture.Destination).ShouldBe(expected);
        }
        fixture.AssertOnlyDestinationRemains();
    }

    [Theory]
    [InlineData(1)] // EPERM: includes filesystems that prohibit hard links.
    [InlineData(2)] // ENOENT
    [InlineData(17)] // EEXIST
    [InlineData(95)] // Linux EOPNOTSUPP
    public void LinkCompletedFile_NativeFailure_ReportsErrnoWithoutFallbackOrDeletingSource(int errno)
    {
        using var fixture = new ForwardingFileSystem();
        var source = Path.Combine(fixture.DirectoryPath, "completed.tmp");
        fixture.Real.File.WriteAllText(source, ReadBundledPrompt());
        var attempts = 0;

        var error = Should.Throw<IOException>(() => SkillsPromptSeeder.LinkCompletedFile(
            source, fixture.Destination, (_, _) => { attempts++; return errno; }));

        attempts.ShouldBe(1);
        error.HResult.ShouldBe(errno);
        error.InnerException.ShouldBeOfType<System.ComponentModel.Win32Exception>().NativeErrorCode.ShouldBe(errno);
        error.Message.ShouldContain("link");
        error.Message.ShouldContain(source);
        error.Message.ShouldContain(fixture.Destination);
        error.Message.ShouldContain("hard links");
        fixture.Real.File.ReadAllText(source).ShouldBe(ReadBundledPrompt());
        fixture.Real.File.Exists(fixture.Destination).ShouldBeFalse();
    }

    [Fact]
    public void PublishCompletedFile_MockCollision_PreservesOperatorAndDoesNotRequireNativePaths()
    {
        var fs = new System.IO.Abstractions.TestingHelpers.MockFileSystem();
        var directory = fs.Path.GetFullPath("mock-prompts");
        fs.Directory.CreateDirectory(directory);
        var source = fs.Path.Combine(directory, "completed.tmp");
        var destination = fs.Path.Combine(directory, PromptFileName);
        fs.File.WriteAllText(source, ReadBundledPrompt());
        fs.File.WriteAllText(destination, OperatorContent);

        var error = Should.Throw<IOException>(() => SkillsPromptSeeder.PublishCompletedFile(fs, source, destination));

        // TestingHelpers 22.1.1 throws a generic IOException for Move collisions, not EEXIST.
        // Do not broaden production collision classification to swallow unrelated generic I/O errors.
        error.HResult.ShouldBe(unchecked((int)0x80131620));
        fs.File.ReadAllText(destination).ShouldBe(OperatorContent);
        fs.File.ReadAllText(source).ShouldBe(ReadBundledPrompt());
        SkillsPromptSeeder.EnsureSharedPromptSeed(directory, fs);
        fs.File.ReadAllText(destination).ShouldBe(OperatorContent);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(95)]
    public void EnsureSharedPromptSeed_NativePermanentFailure_DoesNotRetryAndCleansOwnedTemp(int errno)
    {
        using var fixture = new ForwardingFileSystem();
        fixture.BeforeMove = (source, destination, _) =>
            SkillsPromptSeeder.LinkCompletedFile(source, destination, (_, _) => errno);
        var clock = new RetryClock();

        var error = Should.Throw<IOException>(() => SkillsPromptSeeder.EnsureSharedPromptSeed(
            fixture.DirectoryPath, fixture.FileSystem, null, clock, clock.Delay));

        error.HResult.ShouldBe(errno);
        fixture.MoveAttempts.ShouldBe(1);
        clock.Delays.ShouldBe(0);
        fixture.Real.Directory.GetFiles(fixture.DirectoryPath).ShouldBeEmpty();
    }

    private sealed class RetryClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public int Delays { get; private set; }
        public bool AdvanceOnDelay { get; init; } = true;
        public TimeSpan? DelayAdvance { get; init; }

        public void Delay(TimeSpan duration)
        {
            Delays++;
            if (AdvanceOnDelay)
                _timestamp += (DelayAdvance ?? duration).Ticks;
        }
    }

    private static IOException SharingViolation() =>
        new("injected publication sharing violation", unchecked((int)0x80070020));

    private static string ReadBundledPrompt()
    {
        using var stream = typeof(SkillsPromptSeeder).Assembly.GetManifestResourceStream(
            "BotNexus.Extensions.Skills.Resources.Prompts.optimize-shared-skills.prompt.md")
            ?? throw new InvalidOperationException("Bundled prompt resource missing from test assembly dependency.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// A real filesystem with deterministic callbacks at the write and no-overwrite move boundaries.
    /// Only file operations are intercepted; all unconfigured operations call the real FileWrapper.
    /// The injected filesystem owns the no-replace publication contract. After observing or
    /// faulting Move, this real-backed adapter uses the production publisher (including Unix link).
    /// </summary>
    private sealed class ForwardingFileSystem : IDisposable
    {
        public FileSystem Real { get; } = new();
        public IFileSystem FileSystem { get; }
        public FileWrapper File { get; }
        public DirectoryWrapper Directory { get; }
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "botnexus-4861-" + Guid.NewGuid().ToString("N"));
        public string Destination => Path.Combine(DirectoryPath, PromptFileName);
        public Action<string>? DuringWrite { get; set; }
        public Action<string, string, bool>? BeforeMove { get; set; }
        public int MoveAttempts { get; private set; }

        public ForwardingFileSystem()
        {
            Real.Directory.CreateDirectory(DirectoryPath);
            File = Substitute.ForPartsOf<FileWrapper>(Real);
            Directory = Substitute.ForPartsOf<DirectoryWrapper>(Real);
            FileSystem = Substitute.For<IFileSystem>();
            FileSystem.File.Returns(File);
            FileSystem.Path.Returns(Real.Path);
            FileSystem.Directory.Returns(Directory);
            FileSystem.FileInfo.Returns(Real.FileInfo);
            FileSystem.FileStream.Returns(Real.FileStream);
            FileSystem.DirectoryInfo.Returns(Real.DirectoryInfo);

            File.When(x => x.WriteAllText(Arg.Any<string>(), Arg.Any<string>())).DoNotCallBase();
            File.When(x => x.WriteAllText(Arg.Any<string>(), Arg.Any<string>())).Do(call =>
                WriteWithObservation(call.ArgAt<string>(0), call.ArgAt<string>(1), Encoding.UTF8));
            File.When(x => x.WriteAllText(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Encoding>())).DoNotCallBase();
            File.When(x => x.WriteAllText(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Encoding>())).Do(call =>
                WriteWithObservation(call.ArgAt<string>(0), call.ArgAt<string>(1), call.ArgAt<Encoding>(2)));
            File.When(x => x.Move(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())).DoNotCallBase();
            File.When(x => x.Move(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())).Do(call =>
                MoveWithObservation(call.ArgAt<string>(0), call.ArgAt<string>(1), call.ArgAt<bool>(2)));
            File.When(x => x.Move(Arg.Any<string>(), Arg.Any<string>())).DoNotCallBase();
            File.When(x => x.Move(Arg.Any<string>(), Arg.Any<string>())).Do(call =>
                MoveWithObservation(call.ArgAt<string>(0), call.ArgAt<string>(1), false));
        }

        private void WriteWithObservation(string path, string content, Encoding encoding)
        {
            Real.File.WriteAllText(path, content[..Math.Min(16, content.Length)], encoding);
            DuringWrite?.Invoke(path);
            Real.File.WriteAllText(path, content, encoding);
        }

        private void MoveWithObservation(string source, string destination, bool overwrite)
        {
            MoveAttempts++;
            // Deterministic safety bound also makes an accidental unbounded retry fail the test.
            if (MoveAttempts > 20)
                throw new InvalidOperationException("Publication exceeded the test's bounded retry safety limit.");
            BeforeMove?.Invoke(source, destination, overwrite);
            overwrite.ShouldBeFalse();
            SkillsPromptSeeder.PublishCompletedFile(Real, source, destination);
        }

        public void AssertOnlyDestinationRemains() =>
            Real.Directory.GetFiles(DirectoryPath).ShouldBe(new[] { Destination });

        public void Dispose() => Real.Directory.Delete(DirectoryPath, recursive: true);
    }
}
