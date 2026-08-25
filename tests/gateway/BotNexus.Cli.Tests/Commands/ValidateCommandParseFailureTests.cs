using BotNexus.Cli.Commands;

namespace BotNexus.Cli.Tests.Commands;

/// <summary>
/// The loader falls back to defaults when config.json cannot be parsed, which is right for the
/// gateway - it stays up - but meant `botnexus validate` was validating that pristine fallback
/// rather than the operator's file, and reporting VALID for a config the gateway was silently
/// ignoring. That is the exact opposite of what the command exists to tell you.
/// </summary>
public sealed class ValidateCommandParseFailureTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("botnexus-validate-tests").FullName;

    private string ConfigPath => Path.Combine(_directory, "config.json");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("")]
    [InlineData("[1, 2, 3")]
    [InlineData("{\"gateway\": {\"listenUrl\": \"http://localhost:5005\"}")]  // truncated - one brace short
    public async Task ExecuteAsync_UnparseableConfig_FailsRatherThanValidatingTheFallback(string contents)
    {
        await File.WriteAllTextAsync(ConfigPath, contents);

        var exitCode = await new ValidateCommand().ExecuteAsync(ConfigPath, verbose: false, CancellationToken.None);

        exitCode.ShouldBe(1);
    }

    [Fact]
    public async Task ExecuteAsync_ValidConfig_Passes()
    {
        await File.WriteAllTextAsync(ConfigPath, """
            {
              "version": 1,
              "gateway": { "listenUrl": "http://localhost:5005" }
            }
            """);

        var exitCode = await new ValidateCommand().ExecuteAsync(ConfigPath, verbose: false, CancellationToken.None);

        exitCode.ShouldBe(0);
    }

    // Trailing commas are tolerated by the gateway's own reader, so validate must not reject what
    // the gateway would happily load.
    [Fact]
    public async Task ExecuteAsync_TrailingComma_IsAccepted()
    {
        await File.WriteAllTextAsync(ConfigPath, """
            {
              "version": 1,
              "gateway": { "listenUrl": "http://localhost:5005" },
            }
            """);

        var exitCode = await new ValidateCommand().ExecuteAsync(ConfigPath, verbose: false, CancellationToken.None);

        exitCode.ShouldBe(0);
    }

    [Fact]
    public async Task ExecuteAsync_MissingFile_StillReportsNotFound()
    {
        var exitCode = await new ValidateCommand().ExecuteAsync(
            Path.Combine(_directory, "absent.json"), verbose: false, CancellationToken.None);

        exitCode.ShouldBe(1);
    }
}
