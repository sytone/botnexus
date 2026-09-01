using System.Text.Json;
using System.Net;
using System.Net.Sockets;
using System.Text;
using BotNexus.Cli.Commands;
using BotNexus.Gateway.Configuration;
using Spectre.Console;

namespace BotNexus.Cli.Tests.Commands;

public sealed class PromptCommandsTests
{
    [Fact]
    public void TryParseParameters_ParsesKeyValuePairs()
    {
        var ok = PromptCommands.TryParseParameters(
            ["owner=Hermes", "project=botnexus"],
            out var parameters,
            out var error);

        ok.ShouldBeTrue();
        error.ShouldBeNull();
        parameters["owner"].ShouldBe("Hermes");
        parameters["project"].ShouldBe("botnexus");
    }

    [Fact]
    public void TryParseParameters_RejectsInvalidFormat()
    {
        var ok = PromptCommands.TryParseParameters(["invalid"], out _, out var error);

        ok.ShouldBeFalse();
        error.ShouldNotBeNull();
        error.ShouldContain("Use --param key=value");
    }

    [Fact]
    public async Task ExecuteRenderAsync_RendersTemplateFromConfig()
    {
        var tempHome = Path.Combine(Path.GetTempPath(), $"botnexus-prompt-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempHome);
        var configPath = Path.Combine(tempHome, "config.json");

        var config = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                DefaultAgentId = "agent-a"
            },
            PromptTemplates = new Dictionary<string, PromptTemplateConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["daily-status"] = new()
                {
                    Prompt = "Status for {{project}} by {{owner}}",
                    Defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["project"] = "BotNexus"
                    }
                }
            }
        };

        await File.WriteAllTextAsync(
            configPath,
            JsonSerializer.Serialize(config, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }));

        try
        {
            var command = new PromptCommands();
            var writer = new StringWriter();
            var originalOut = Console.Out;
            Console.SetOut(writer);
            var result = await command.ExecuteRenderAsync(
                configPath,
                "agent-a",
                "daily-status",
                ["owner=Hermes"],
                verbose: false,
                runMode: false,
                CancellationToken.None);
            Console.SetOut(originalOut);

            result.ShouldBe(0);
            writer.ToString().ShouldContain("Status for BotNexus by Hermes");
        }
        finally
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            if (Directory.Exists(tempHome))
                Directory.Delete(tempHome, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteListAsync_ListsConfigAndSharedFileTemplates()
    {
        var tempHome = Path.Combine(Path.GetTempPath(), $"botnexus-prompt-list-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempHome);
        Directory.CreateDirectory(Path.Combine(tempHome, "prompts"));
        var configPath = Path.Combine(tempHome, "config.json");
        await File.WriteAllTextAsync(
            Path.Combine(tempHome, "prompts", "shared-template.prompt.json"),
            """
            {
              "name": "shared-template",
              "prompt": "Shared {{name}}"
            }
            """);
        await File.WriteAllTextAsync(
            Path.Combine(tempHome, "prompts", "status-template.prompt.md"),
            """
            ---
            name: status-template
            ---
            # Status for {{name}}
            """);

        var config = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                DefaultAgentId = "agent-a"
            },
            PromptTemplates = new Dictionary<string, PromptTemplateConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["config-template"] = new()
                {
                    Prompt = "Config {{name}}"
                }
            }
        };

        await File.WriteAllTextAsync(
            configPath,
            JsonSerializer.Serialize(config, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }));

        var writer = new StringWriter();
        var originalOut = Console.Out;
        try
        {
            Console.SetOut(writer);
            var command = new PromptCommands();
            var result = await command.ExecuteListAsync(
                configPath,
                agentId: null,
                verbose: false,
                CancellationToken.None);
            Console.SetOut(originalOut);

            result.ShouldBe(0);
            writer.ToString().ShouldContain("config-template");
            writer.ToString().ShouldContain("shared-template");
            writer.ToString().ShouldContain("status-template");
        }
        finally
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            if (Directory.Exists(tempHome))
                Directory.Delete(tempHome, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteRenderAsync_RendersMarkdownTemplateWithMultilineFormatting()
    {
        var tempHome = Path.Combine(Path.GetTempPath(), $"botnexus-prompt-md-render-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempHome);
        Directory.CreateDirectory(Path.Combine(tempHome, "prompts"));
        var configPath = Path.Combine(tempHome, "config.json");

        await File.WriteAllTextAsync(
            Path.Combine(tempHome, "prompts", "status-report.prompt.md"),
            """
            ---
            name: status-report
            parameters:
              owner:
                default: team@example.com
            ---
            # Weekly Status: {{project}}

            ## Details

            - Owner: {{owner}}
            - Summary: {{summary}}

            1. Accomplishments
            2. Risks
            """);

        var config = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                DefaultAgentId = "agent-a"
            }
        };

        await File.WriteAllTextAsync(
            configPath,
            JsonSerializer.Serialize(config, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }));

        var writer = new StringWriter();
        var originalOut = Console.Out;
        try
        {
            Console.SetOut(writer);
            var command = new PromptCommands();
            var result = await command.ExecuteRenderAsync(
                configPath,
                "agent-a",
                "status-report",
                ["project=BotNexus", "summary=All tests passed"],
                verbose: false,
                runMode: false,
                CancellationToken.None);
            Console.SetOut(originalOut);

            result.ShouldBe(0);
            writer.ToString().ShouldContain("# Weekly Status: BotNexus");
            writer.ToString().ShouldContain("- Owner: team@example.com");
            writer.ToString().ShouldContain("1. Accomplishments");
        }
        finally
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            if (Directory.Exists(tempHome))
                Directory.Delete(tempHome, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteRunAsync_RendersMarkdownTemplateAndPostsRenderedPrompt()
    {
        var tempHome = Path.Combine(Path.GetTempPath(), $"botnexus-prompt-md-run-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempHome);
        Directory.CreateDirectory(Path.Combine(tempHome, "prompts"));
        var configPath = Path.Combine(tempHome, "config.json");
        await File.WriteAllTextAsync(
            Path.Combine(tempHome, "prompts", "status-report.prompt.md"),
            """
            ---
            name: status-report
            parameters:
              owner:
                default: team@example.com
            ---
            # Weekly Status: {{project}}

            - Owner: {{owner}}
            - Summary: {{summary}}
            """);

        var config = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                DefaultAgentId = "agent-a"
            }
        };

        await File.WriteAllTextAsync(
            configPath,
            JsonSerializer.Serialize(config, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }));

        string? capturedBody = null;
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        var serverTask = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
            capturedBody = await reader.ReadToEndAsync();

            var responseJson = """{"sessionId":"session-1","content":"gateway reply"}""";
            var responseBytes = Encoding.UTF8.GetBytes(responseJson);
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = responseBytes.Length;
            await context.Response.OutputStream.WriteAsync(responseBytes);
            context.Response.Close();
        });

        var writer = new StringWriter();
        var originalOut = Console.Out;
        try
        {
            Console.SetOut(writer);
            var command = new PromptCommands();
            var result = await command.ExecuteRunAsync(
                configPath,
                "agent-a",
                "status-report",
                ["project=BotNexus", "summary=All tests passed"],
                sessionId: "session-1",
                gatewayUrlOverride: $"http://127.0.0.1:{port}",
                verbose: false,
                CancellationToken.None);

            await serverTask;
            Console.SetOut(originalOut);

            result.ShouldBe(0);
            capturedBody.ShouldNotBeNull();
            var payload = JsonSerializer.Deserialize<Dictionary<string, string?>>(capturedBody!);
            payload.ShouldNotBeNull();
            payload["agentId"].ShouldBe("agent-a");
            payload["sessionId"].ShouldBe("session-1");
            var renderedMessage = payload["message"]!.Replace("\r\n", "\n");
            renderedMessage.ShouldContain("# Weekly Status: BotNexus");
            renderedMessage.ShouldContain("- Owner: team@example.com");
            renderedMessage.ShouldContain("- Summary: All tests passed");
            writer.ToString().ShouldContain("gateway reply");
        }
        finally
        {
            listener.Stop();
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            if (Directory.Exists(tempHome))
                Directory.Delete(tempHome, recursive: true);
        }
    }

    /// <summary>
    /// Clause 1 of #2747: <c>prompt run</c> used to POST <c>/api/chat</c> from a bare HttpClient, so
    /// an operator-supplied <c>--gateway-url</c> was contacted with no credential and no refusal. The
    /// prompt still renders; the request is what must not leave.
    /// </summary>
    [Fact]
    public async Task ExecuteRunAsync_RefusesNonLoopbackGatewayUrl_WithoutExplicitToken()
    {
        var tempHome = Path.Combine(Path.GetTempPath(), $"botnexus-prompt-refuse-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempHome, "prompts"));
        var configPath = Path.Combine(tempHome, "config.json");
        await File.WriteAllTextAsync(
            Path.Combine(tempHome, "prompts", "status-report.prompt.md"),
            """
            ---
            name: status-report
            ---
            Status for {{project}}
            """);
        await File.WriteAllTextAsync(
            configPath,
            JsonSerializer.Serialize(
                new PlatformConfig { Gateway = new GatewaySettingsConfig { DefaultAgentId = "agent-a" } },
                new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

        try
        {
            // TEST-NET-3 (RFC 5737): reserved, non-routable. A regression that actually sends the
            // request fails on connect instead of reaching a real host - the refusal must come first.
            var result = await new PromptCommands().ExecuteRunAsync(
                configPath,
                "agent-a",
                "status-report",
                ["project=BotNexus"],
                sessionId: null,
                gatewayUrlOverride: "http://203.0.113.10:5005",
                verbose: false,
                CancellationToken.None,
                token: null);

            result.ShouldBe(1,
                "A non-loopback --gateway-url with no --token must be refused before the POST is sent. " +
                "Any other outcome means the rendered prompt was shipped unauthenticated to a host " +
                "named on the command line. See #2747 clause 1.");
        }
        finally
        {
            if (Directory.Exists(tempHome))
                Directory.Delete(tempHome, recursive: true);
        }
    }

    [Fact]
    public void GetEmbeddedSampleTemplateNames_ReturnsBundledTemplates()
    {
        var templateNames = PromptCommands.GetEmbeddedSampleTemplateNames();

        templateNames.Count.ShouldBeGreaterThan(0);
        templateNames.ShouldContain("sample-greeting.prompt.md");
        templateNames.ShouldContain("sample-simple-greeting.prompt.json");
    }

    [Fact]
    public async Task ExecuteCreateSamplesAsync_CopiesBundledSampleTemplates()
    {
        var tempHome = Path.Combine(Path.GetTempPath(), $"botnexus-create-samples-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempHome);

        var promptsDir = Path.Combine(tempHome, "prompts");
        var bundledTemplates = PromptCommands.GetEmbeddedSampleTemplateNames();

        try
        {
            var command = new PromptCommands();
            var result = await command.ExecuteCreateSamplesAsync(tempHome, CancellationToken.None);

            result.ShouldBe(0);
            Directory.Exists(promptsDir).ShouldBeTrue("prompts directory should be created");

            var copiedFiles = Directory.GetFiles(promptsDir, "*.prompt.*");
            copiedFiles.Length.ShouldBe(bundledTemplates.Count);
            foreach (var bundledTemplate in bundledTemplates)
                copiedFiles.Select(Path.GetFileName).ShouldContain(bundledTemplate);
        }
        finally
        {
            if (Directory.Exists(tempHome))
                Directory.Delete(tempHome, recursive: true);
        }
    }

    /// <summary>
    /// #3739: a present-but-blank <c>--agent</c> or <c>--session</c> used to be indistinguishable from
    /// an omitted one, because every downstream consumer tests <c>IsNullOrWhiteSpace</c>. The blank
    /// <c>--agent</c> resolved to <c>gateway.defaultAgentId</c> and the blank <c>--session</c> made the
    /// gateway mint a session the caller has no id for, so the turn ran somewhere the caller never saw.
    /// </summary>
    [Theory]
    [InlineData("", null, "--agent")]
    [InlineData("   ", null, "--agent")]
    [InlineData("\t", null, "--agent")]
    [InlineData(null, "", "--session")]
    [InlineData(null, "   ", "--session")]
    [InlineData(null, "\t", "--session")]
    public async Task ExecuteRunAsync_RejectsBlankSelector_WithoutDispatchingATurn(
        string? agentId,
        string? sessionId,
        string expectedFlagInMessage)
    {
        using var harness = await BlankSelectorHarness.CreateAsync();
        // PromptCommands writes diagnostics through Spectre's AnsiConsole, which binds its writer at
        // construction - Console.SetOut after the fact captures nothing. Swap AnsiConsole.Console
        // itself, the pattern the rest of this test project already uses.
        var writer = new StringWriter();
        var originalConsole = AnsiConsole.Console;
        AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(writer),
            Interactive = InteractionSupport.No
        });

        try
        {
            var result = await new PromptCommands().ExecuteRunAsync(
                harness.ConfigPath,
                agentId,
                "status-report",
                ["project=BotNexus"],
                sessionId,
                gatewayUrlOverride: harness.GatewayUrl,
                verbose: false,
                CancellationToken.None);

            result.ShouldBe(1,
                $"A present-but-blank {expectedFlagInMessage} is a caller error and must fail the run. " +
                "Exit 0 means the turn was silently redirected to the default agent or a freshly " +
                "minted session. See #3739.");

            // Non-vacuity: exit 1 on its own is satisfiable by a config-load or render failure, neither
            // of which is this defect. These two assertions pin the actual contract - nothing reached
            // the gateway, and the message names the flag the caller got wrong.
            harness.RequestWasReceived.ShouldBeFalse(
                "The refusal must happen before the turn is dispatched, not after the gateway has " +
                "already run it against the wrong target.");
            writer.ToString().ShouldContain(expectedFlagInMessage);
        }
        finally
        {
            AnsiConsole.Console = originalConsole;
        }
    }

    /// <summary>
    /// #3739 AC3: only a <em>present-and-blank</em> selector is rejected. An omitted selector is
    /// <c>null</c> and keeps its existing meaning - default agent, gateway-minted session. Without this
    /// test a validator that also rejected <c>null</c> would satisfy the theory above while breaking
    /// every ordinary invocation.
    /// </summary>
    [Fact]
    public async Task ExecuteRunAsync_OmittedSelectors_StillResolveDefaultAgentAndNullSession()
    {
        using var harness = await BlankSelectorHarness.CreateAsync();
        var writer = new StringWriter();
        var originalConsole = AnsiConsole.Console;
        AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(writer),
            Interactive = InteractionSupport.No
        });

        try
        {
            var result = await new PromptCommands().ExecuteRunAsync(
                harness.ConfigPath,
                agentId: null,
                "status-report",
                ["project=BotNexus"],
                sessionId: null,
                gatewayUrlOverride: harness.GatewayUrl,
                verbose: false,
                CancellationToken.None);

            result.ShouldBe(0);
            harness.RequestWasReceived.ShouldBeTrue("An omitted selector must not block the turn.");

            var payload = JsonSerializer.Deserialize<Dictionary<string, string?>>(harness.CapturedBody!);
            payload.ShouldNotBeNull();
            payload["agentId"].ShouldBe("agent-a", "An omitted --agent still resolves gateway.defaultAgentId.");
            (payload.TryGetValue("sessionId", out var sentSessionId) ? sentSessionId : null)
                .ShouldBeNull("An omitted --session is still the gateway's job to mint.");
        }
        finally
        {
            AnsiConsole.Console = originalConsole;
        }
    }

    /// <summary>
    /// Loopback gateway that records whether <c>prompt run</c> dispatched anything. A real listener is
    /// used rather than an unreachable address so that "nothing was sent" is a positive observation
    /// about the CLI rather than an artefact of a host that could not be contacted anyway.
    /// </summary>
    private sealed class BlankSelectorHarness : IDisposable
    {
        private readonly string _tempHome;
        private readonly HttpListener _listener;

        private BlankSelectorHarness(string tempHome, string configPath, string gatewayUrl, HttpListener listener)
        {
            _tempHome = tempHome;
            _listener = listener;
            ConfigPath = configPath;
            GatewayUrl = gatewayUrl;
            _ = Task.Run(async () =>
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    using (var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding))
                    {
                        CapturedBody = await reader.ReadToEndAsync();
                    }

                    var responseBytes = Encoding.UTF8.GetBytes(
                        "{\"sessionId\":\"session-minted\",\"content\":\"gateway reply\"}");
                    context.Response.StatusCode = 200;
                    context.Response.ContentType = "application/json";
                    context.Response.ContentLength64 = responseBytes.Length;
                    await context.Response.OutputStream.WriteAsync(responseBytes);
                    context.Response.Close();
                }
                catch (HttpListenerException)
                {
                    // Listener closed with no request pending - the expected blank-selector outcome.
                }
                catch (ObjectDisposedException)
                {
                }
            });
        }

        public string ConfigPath { get; }

        public string GatewayUrl { get; }

        public string? CapturedBody { get; private set; }

        public bool RequestWasReceived => CapturedBody is not null;

        public static async Task<BlankSelectorHarness> CreateAsync()
        {
            var tempHome = Path.Combine(Path.GetTempPath(), $"botnexus-prompt-blank-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path.Combine(tempHome, "prompts"));
            await File.WriteAllTextAsync(
                Path.Combine(tempHome, "prompts", "status-report.prompt.md"),
                "---\nname: status-report\n---\nStatus for {{project}}\n");

            var configPath = Path.Combine(tempHome, "config.json");
            await File.WriteAllTextAsync(
                configPath,
                JsonSerializer.Serialize(
                    new PlatformConfig { Gateway = new GatewaySettingsConfig { DefaultAgentId = "agent-a" } },
                    new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();

            return new BlankSelectorHarness(tempHome, configPath, $"http://127.0.0.1:{port}", listener);
        }

        public void Dispose()
        {
            try
            {
                _listener.Close();
            }
            catch (ObjectDisposedException)
            {
            }

            if (Directory.Exists(_tempHome))
                Directory.Delete(_tempHome, recursive: true);
        }
    }
}
