using System.Diagnostics;
using System.Text.Json;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Issue #4040: the shared panel splitter must expose and implement its separator semantics,
/// not merely carry a role while remaining pointer-only. These tests execute the production
/// splitter.js under Node so removing keyboard admission or adjustment fails behaviorally.
/// </summary>
public sealed class SplitterKeyboardAccessibilityTests
{
    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".."));

    private static readonly string SplitterJsPath = Path.Combine(
        RepoRoot, "src", "extensions", "BotNexus.Extensions.Channels.SignalR.BlazorClient",
        "wwwroot", "js", "splitter.js");

    private static readonly string AppCssPath = Path.Combine(
        RepoRoot, "src", "extensions", "BotNexus.Extensions.Channels.SignalR.BlazorClient",
        "wwwroot", "css", "app.css");

    [Fact]
    public void Init_makes_separator_keyboard_reachable_and_exposes_current_bounds_and_pane()
    {
        using var result = RunScenario();
        var initial = result.RootElement.GetProperty("initial");

        Assert.Equal("0", initial.GetProperty("tabindex").GetString());
        Assert.Equal("pane", initial.GetProperty("controls").GetString());
        Assert.Equal("180", initial.GetProperty("min").GetString());
        Assert.Equal("400", initial.GetProperty("max").GetString());
        Assert.Equal("240", initial.GetProperty("now").GetString());
        Assert.Equal("ArrowLeft ArrowRight Home End", initial.GetProperty("shortcuts").GetString());
    }

    [Fact]
    public void Arrow_and_boundary_keys_resize_through_the_persisted_bounded_width_path()
    {
        using var result = RunScenario();
        var root = result.RootElement;

        AssertWidth(root.GetProperty("right"), expectedWidth: "250px", expectedStored: "250", expectedNow: "250");
        AssertWidth(root.GetProperty("home"), expectedWidth: "180px", expectedStored: "180", expectedNow: "180");
        AssertWidth(root.GetProperty("end"), expectedWidth: "400px", expectedStored: "400", expectedNow: "400");
        Assert.True(root.GetProperty("prevented").GetBoolean());
    }

    [Fact]
    public void Pointer_resize_updates_the_same_accessible_value()
    {
        using var result = RunScenario();
        var pointer = result.RootElement.GetProperty("pointer");

        AssertWidth(pointer, expectedWidth: "275px", expectedStored: "275", expectedNow: "275");
    }

    [Fact]
    public void Separator_has_a_visible_keyboard_focus_indicator()
    {
        var css = File.ReadAllText(AppCssPath);

        Assert.Contains(".panel-splitter:focus-visible", css, StringComparison.Ordinal);
        Assert.Contains("outline", css, StringComparison.Ordinal);
    }

    private static void AssertWidth(JsonElement observation, string expectedWidth, string expectedStored, string expectedNow)
    {
        Assert.Equal(expectedWidth, observation.GetProperty("width").GetString());
        Assert.Equal(expectedStored, observation.GetProperty("stored").GetString());
        Assert.Equal(expectedNow, observation.GetProperty("now").GetString());
    }

    private static JsonDocument RunScenario()
    {
        Assert.True(File.Exists(SplitterJsPath), $"splitter.js not found at {SplitterJsPath}");

        var driver = $$"""
            function target() {
              return {
                listeners: {}, style: {}, attrs: {}, classList: { add: function () {}, remove: function () {} },
                addEventListener: function (name, fn) { this.listeners[name] = fn; },
                removeEventListener: function (name) { delete this.listeners[name]; },
                setAttribute: function (name, value) { this.attrs[name] = String(value); },
                getAttribute: function (name) { return this.attrs[name] || null; }
              };
            }
            var pane = target();
            pane.id = 'pane';
            pane.width = 240;
            pane.getBoundingClientRect = function () { return { width: parseInt(this.style.width || this.width, 10) }; };
            var splitter = target();
            splitter.previousElementSibling = pane;
            var container = target();
            container.querySelector = function () { return splitter; };
            container.getBoundingClientRect = function () { return { width: 1000 }; };
            var documentTarget = target();
            documentTarget.body = { style: {} };
            documentTarget.getElementById = function (id) { return id === 'container' ? container : null; };
            globalThis.window = globalThis;
            globalThis.document = documentTarget;
            var values = {};
            globalThis.localStorage = {
              getItem: function (key) { return values[key] || null; },
              setItem: function (key, value) { values[key] = String(value); }
            };
            globalThis.ResizeObserver = function (callback) {
              this.callback = callback;
              this.observe = function () {};
              this.disconnect = function () {};
            };
            require({{JsonSerializer.Serialize(SplitterJsPath)}});
            window.BotNexus.splitter.init('container', 'width-key', 240, 180, 0.4);
            function state() {
              return { width: pane.style.width, stored: values['width-key'] || null, now: splitter.attrs['aria-valuenow'] || null };
            }
            var initial = {
              tabindex: splitter.attrs.tabindex || null,
              controls: splitter.attrs['aria-controls'] || null,
              min: splitter.attrs['aria-valuemin'] || null,
              max: splitter.attrs['aria-valuemax'] || null,
              now: splitter.attrs['aria-valuenow'] || null,
              shortcuts: splitter.attrs['aria-keyshortcuts'] || null
            };
            var prevented = false;
            splitter.listeners.keydown({ key: 'ArrowRight', preventDefault: function () { prevented = true; } });
            var right = state();
            splitter.listeners.keydown({ key: 'Home', preventDefault: function () {} });
            var home = state();
            splitter.listeners.keydown({ key: 'End', preventDefault: function () {} });
            var end = state();
            pane.style.width = '240px';
            splitter.listeners.mousedown({ button: 0, clientX: 10, preventDefault: function () {} });
            documentTarget.listeners.mousemove({ clientX: 45 });
            var pointer = state();
            process.stdout.write(JSON.stringify({ initial: initial, right: right, home: home, end: end, pointer: pointer, prevented: prevented }));
            """;

        var tempDir = Path.Combine(Path.GetTempPath(), "botnexus-splitter-4040-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var driverPath = Path.Combine(tempDir, "driver.js");
            File.WriteAllText(driverPath, driver);
            var startInfo = new ProcessStartInfo("node", driverPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(startInfo);
            Assert.NotNull(process);
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, $"node driver failed (exit {process.ExitCode}). stderr: {stderr}");
            return JsonDocument.Parse(stdout);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch (IOException) { }
        }
    }
}
