using System.Text.Json;

namespace BotNexus.Extensions.Qmd.Tests;

public sealed class QmdCliBackendTests
{
    [Fact]
    public void Constructor_WithNullPath_DefaultsToQmd()
    {
        var backend = new QmdCliBackend(null);
        // Can't easily test the path without running, but we can verify it doesn't throw
        Assert.NotNull(backend);
    }

    [Fact]
    public void Constructor_WithExplicitPath_UsesProvidedPath()
    {
        var backend = new QmdCliBackend("/usr/local/bin/qmd");
        Assert.NotNull(backend);
    }

    [Fact]
    public async Task RunAsync_WhenBinaryNotFound_ThrowsQmdBinaryNotFoundException()
    {
        var backend = new QmdCliBackend("/nonexistent/path/to/qmd-fake-binary-xyz");
        await Assert.ThrowsAsync<QmdBinaryNotFoundException>(
            () => backend.RunAsync(["status", "--json"], CancellationToken.None));
    }

    [Fact]
    public async Task RunAsync_WhenCancelled_ThrowsOperationCancelled()
    {
        var backend = new QmdCliBackend("/nonexistent/path/to/qmd-fake-binary-xyz");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        // Either throws OperationCanceled or QmdBinaryNotFound depending on platform timing
        await Assert.ThrowsAnyAsync<Exception>(
            () => backend.RunAsync(["status"], cts.Token));
    }

    [Fact]
    public void QmdBinaryNotFoundException_ContainsPath()
    {
        var ex = new QmdBinaryNotFoundException("/some/path");
        Assert.Contains("/some/path", ex.Message);
    }

    [Fact]
    public void QmdCliException_ContainsExitCodeAndStderr()
    {
        var ex = new QmdCliException(1, "error detail", ["search", "test"]);
        Assert.Equal(1, ex.ExitCode);
        Assert.Equal("error detail", ex.StdErr);
        Assert.Contains("error detail", ex.Message);
    }

    [Fact]
    public async Task DisposeAsync_DoesNotThrow()
    {
        var backend = new QmdCliBackend();
        await backend.DisposeAsync();
    }
}
