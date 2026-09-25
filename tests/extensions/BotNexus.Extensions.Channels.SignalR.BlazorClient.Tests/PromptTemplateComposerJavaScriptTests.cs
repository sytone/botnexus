namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class PromptTemplateComposerJavaScriptTests
{
    [Fact]
    public async Task Replacement_executes_real_selection_and_context_guards_in_node()
    {
        var scriptPath = Path.Combine(AppContext.BaseDirectory, "wwwroot", "js", "chat.js");
        var script = File.ReadAllText(scriptPath);
        script.ShouldContain("element.setRangeText(text, start, end, 'end')");
        script.ShouldContain("event.key !== 'Tab'");
        script.ShouldContain("event.preventDefault()");
        var testPath = Path.Combine(AppContext.BaseDirectory, "wwwroot", "js", "prompt-template-composer.test.mjs");
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "node",
            ArgumentList = { testPath, scriptPath },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        });
        process.ShouldNotBeNull();
        await process.WaitForExitAsync();
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        process.ExitCode.ShouldBe(0, $"Node output: {output}{Environment.NewLine}{error}");
    }
}
