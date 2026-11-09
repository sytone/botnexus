using System.IO.Abstractions;
using System.Security.AccessControl;
using System.Security.Principal;
using BotNexus.Extensions.Channels.Matrix.Tests.Fakes;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Extensions.Channels.Matrix.Tests;

public sealed class MatrixAccessTokenReferenceTests : IDisposable
{
    private const string Token = "syt_file_secret_value";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "matrix-token-tests", Guid.NewGuid().ToString("N"));

    public MatrixAccessTokenReferenceTests() => Directory.CreateDirectory(_directory);

    [Theory]
    [InlineData("file:", "\n")]
    [InlineData("FILE:", "\r\n")]
    [InlineData("file:", "")]
    public void Initialize_OwnerOnlyFile_ResolvesTokenWithoutChangingConfiguration(string prefix, string newline)
    {
        var path = WriteSecret(Token + newline);
        var reference = prefix + path;
        var (adapter, factory, config, logger) = CreateAdapter(reference);

        adapter.GetAccountCount().ShouldBe(1);
        factory.Credentials["bot"].AccessToken.Reveal().ShouldBe(Token);
        config.AccessToken.ShouldBe(reference);
        logger.Messages.ShouldNotContain(message => message.Contains(Token, StringComparison.Ordinal));
    }

    [Fact]
    public void Initialize_LegacyLiteral_PreservesToken()
    {
        var (adapter, factory, _, _) = CreateAdapter(Token);
        adapter.GetAccountCount().ShouldBe(1);
        factory.Credentials["bot"].AccessToken.Reveal().ShouldBe(Token);
    }

    [Fact]
    public void Initialize_MissingFile_SkipsAccountWithoutPassingReferenceToFactory() =>
        AssertRejected("file:" + Path.Combine(_directory, "missing"));

    [Theory]
    [InlineData("file:")]
    [InlineData("file:relative-token")]
    [InlineData("file:~another-user/token")]
    public void Initialize_InvalidReference_NeverTreatsItAsLiteral(string reference) => AssertRejected(reference);

    [Theory]
    [InlineData("")]
    [InlineData("\r\n")]
    [InlineData("   \n")]
    [InlineData("file:/unresolved/nested-reference")]
    public void Initialize_InvalidFileContent_SkipsAccountWithoutLeakingContent(string content) =>
        AssertRejected("file:" + WriteSecret(content), content);

    [Fact]
    public void Initialize_OverPermissiveFile_SkipsAccountWithoutLeakingToken()
    {
        var path = WriteSecret(Token);
        if (OperatingSystem.IsWindows())
        {
            var info = new FileInfo(path);
            var security = info.GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                FileSystemRights.Read, AccessControlType.Allow));
            info.SetAccessControl(security);
        }
        else
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        }

        AssertRejected("file:" + path, Token);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Initialize_FileSystemFailure_FailsClosedWithoutLeakingException(bool permissionFailure)
    {
        var path = Path.Combine(_directory, "unreadable");
        var fs = new Mock<IFileSystem>();
        fs.SetupGet(value => value.Path).Returns(new FileSystem().Path);
        fs.Setup(value => value.File.Exists(path)).Returns(true);
        if (!OperatingSystem.IsWindows())
            ConfigureUnixFailure(fs, path, permissionFailure);

        var (adapter, factory, _, logger) = CreateAdapter("file:" + path, fs.Object);
        adapter.GetAccountCount().ShouldBe(0);
        factory.Credentials.ShouldBeEmpty();
        string.Join("\n", logger.Messages).ShouldNotContain(Token);
        fs.Verify(value => value.File.ReadAllText(path),
            !permissionFailure && !OperatingSystem.IsWindows() ? Times.Once() : Times.Never());
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static void ConfigureUnixFailure(Mock<IFileSystem> fs, string path, bool permissionFailure)
    {
        if (permissionFailure)
            fs.Setup(value => value.File.GetUnixFileMode(path)).Throws(new IOException(Token));
        else
        {
            fs.Setup(value => value.File.GetUnixFileMode(path))
                .Returns(UnixFileMode.UserRead | UnixFileMode.UserWrite);
            fs.Setup(value => value.File.ReadAllText(path)).Throws(new UnauthorizedAccessException(Token));
        }
    }

    [Fact]
    public void Initialize_HomeRelativeFile_ResolvesToken()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        home.ShouldNotBeNullOrWhiteSpace();
        var path = Path.Combine(home, "matrix-token-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(path, Token + "\n");
            SecureFilePermissions.RestrictToOwner(path).ShouldBe(FilePermissionOutcome.Applied);
            var (adapter, factory, _, _) = CreateAdapter("file:~/" + Path.GetFileName(path));
            adapter.GetAccountCount().ShouldBe(1);
            factory.Credentials["bot"].AccessToken.Reveal().ShouldBe(Token);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private string WriteSecret(string content)
    {
        var path = Path.Combine(_directory, Guid.NewGuid().ToString("N"));
        File.WriteAllText(path, content);
        SecureFilePermissions.RestrictToOwner(path).ShouldBe(FilePermissionOutcome.Applied);
        return path;
    }

    private static void AssertRejected(string reference, string? content = null)
    {
        var (adapter, factory, _, logger) = CreateAdapter(reference);
        adapter.GetAccountCount().ShouldBe(0);
        factory.Credentials.ShouldBeEmpty();
        logger.Messages.ShouldNotBeEmpty();
        var messages = string.Join("\n", logger.Messages);
        messages.ShouldContain("bot");
        messages.ShouldNotContain(reference);
        if (!string.IsNullOrWhiteSpace(content))
            messages.ShouldNotContain(content);
    }

    private static (MatrixChannelAdapter Adapter, FakeMatrixClientFactory Factory, MatrixAccountConfig Config, RecordingLogger Logger)
        CreateAdapter(string accessToken, IFileSystem? fileSystem = null)
    {
        var config = new MatrixAccountConfig { UserId = "@bot:example.com", AccessToken = accessToken };
        var options = new MatrixChannelOptions { Homeserver = "https://matrix.example.com" };
        options.Agents["bot"] = config;
        var factory = new FakeMatrixClientFactory();
        var logger = new RecordingLogger();
        var adapter = new MatrixChannelAdapter(logger, Options.Create(options), factory, fileSystem: fileSystem);
        return (adapter, factory, config, logger);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private sealed class RecordingLogger : ILogger<MatrixChannelAdapter>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
