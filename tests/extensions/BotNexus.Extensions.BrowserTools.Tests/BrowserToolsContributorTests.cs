using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Abstractions.Text;
using Shouldly;

namespace BotNexus.Extensions.BrowserTools.Tests;

/// <summary>
/// The nine acceptance criteria of #3031: tool surface, subprocess discipline, session isolation.
/// </summary>
/// <remarks>
/// Every test here drives <see cref="FakeAgentBrowserProcessRunner"/> (AC9). No test starts a
/// process, opens a socket, or touches a real browser; the recorded invocations ARE the evidence.
/// </remarks>
public sealed class BrowserToolsContributorTests
{
    private static readonly string WorkspaceRoot =
        Path.Combine(Path.GetTempPath(), "botnexus-browsertools-ws");

    private const string SentinelSecretName = "BOTNEXUS_TEST_SENTINEL_API_KEY";
    private const string SentinelSecretValue = "sentinel-value-that-must-never-reach-the-child";

    // ---- fixtures -------------------------------------------------------------------------

    private static AgentDescriptor Descriptor(string agentId, bool granted, string? configJson = null)
    {
        var extensionConfig = new Dictionary<string, JsonElement>();
        if (granted)
        {
            extensionConfig[BrowserToolsContributor.ExtensionId] =
                JsonDocument.Parse(configJson ?? "{}").RootElement.Clone();
        }

        return new AgentDescriptor
        {
            AgentId = AgentId.From(agentId),
            DisplayName = agentId,
            ModelId = "test-model",
            ApiProvider = "test-provider",
            ExtensionConfig = extensionConfig,
        };
    }

    private static AgentToolContributionContext Context(
        string agentId = "agent-a",
        string sessionId = "session-1",
        bool granted = true,
        string? configJson = null)
        => new(
            Descriptor(agentId, granted, configJson),
            new AgentExecutionContext { SessionId = SessionId.From(sessionId) },
            WorkspaceRoot,
            null!,
            null,
            (_, _) => Task.FromResult<string?>(null));

    private static AgentBrowserResolution Resolved(string path = "/opt/agent-browser")
        => new(AgentBrowserSource.Path, path, null);

    private static BrowserToolsContributor Contributor(
        FakeAgentBrowserProcessRunner runner,
        AgentBrowserResolution? resolution = null,
        Func<string, string?>? readParentVariable = null)
        => new(
            runner,
            new FakeBrowserFileSystem(),
            _ => Task.FromResult(resolution ?? Resolved()),
            readParentVariable ?? (name =>
                name == SentinelSecretName ? SentinelSecretValue : $"value-of-{name}"));

    private static async Task<IReadOnlyList<IAgentTool>> ToolsFor(
        BrowserToolsContributor contributor, AgentToolContributionContext context)
        => (await contributor.ContributeAsync(context)).Tools;

    private static IAgentTool Tool(IReadOnlyList<IAgentTool> tools, string name)
        => tools.Single(t => t.Name == name);

    private static async Task<string> InvokeAsync(
        IAgentTool tool, params (string Key, object? Value)[] arguments)
    {
        var args = arguments.ToDictionary(a => a.Key, a => a.Value, StringComparer.Ordinal);
        var prepared = await tool.PrepareArgumentsAsync(args);
        var result = await tool.ExecuteAsync("call-1", prepared);
        return string.Join("\n", result.Content.Select(c => c.Value));
    }

    // ---- AC1: absent extension config contributes ZERO tools -------------------------------

    [Fact]
    public async Task Ac1_WhenTheExtensionIsAbsentFromTheDescriptor_NoToolsAreContributed()
    {
        var runner = new FakeAgentBrowserProcessRunner();

        var tools = await ToolsFor(Contributor(runner), Context(granted: false));

        tools.ShouldBeEmpty(
            "an agent that was never granted botnexus-browser must not see a browser tool at all.");
    }

    [Fact]
    public async Task Ac1_WhenTheExtensionIsAbsent_NothingIsResolvedAndNoProcessIsLaunched()
    {
        // The zero-tools assertion alone would be satisfied by a contributor that resolves a
        // binary, starts a daemon, and THEN returns an empty list. This pins the absence.
        var runner = new FakeAgentBrowserProcessRunner();
        var resolverCalls = 0;

        var contributor = new BrowserToolsContributor(
            runner,
            new FakeBrowserFileSystem(),
            _ => { resolverCalls++; return Task.FromResult(Resolved()); });

        await ToolsFor(contributor, Context(granted: false));

        resolverCalls.ShouldBe(0);
        runner.Invocations.ShouldBeEmpty();
    }

    [Fact]
    public async Task Ac1_AnEmptyConfigObject_IsAGrantThatTakesEveryDefault()
    {
        // `{}` is an operator's minimal opt-in. Treating it as "absent" would make the documented
        // minimal grant silently do nothing.
        var tools = await ToolsFor(
            Contributor(new FakeAgentBrowserProcessRunner()), Context(configJson: "{}"));

        tools.Count.ShouldBe(5);
    }

    [Fact]
    public async Task Ac1_MalformedExtensionConfig_IsTreatedAsAbsentRatherThanAsDefaults()
    {
        // Falling back to defaults on a typo would GRANT browser access off the back of one.
        var descriptor = new AgentDescriptor
        {
            AgentId = AgentId.From("agent-a"),
            DisplayName = "agent-a",
            ModelId = "m",
            ApiProvider = "p",
            ExtensionConfig = new Dictionary<string, JsonElement>
            {
                [BrowserToolsContributor.ExtensionId] =
                    JsonDocument.Parse("\"not-an-object\"").RootElement.Clone(),
            },
        };

        var context = new AgentToolContributionContext(
            descriptor,
            new AgentExecutionContext { SessionId = SessionId.From("s") },
            WorkspaceRoot,
            null!,
            null,
            (_, _) => Task.FromResult<string?>(null));

        var tools = await ToolsFor(Contributor(new FakeAgentBrowserProcessRunner()), context);

        tools.ShouldBeEmpty();
    }

    // ---- AC2: exactly the five names ------------------------------------------------------

    [Fact]
    public async Task Ac2_ExactlyTheFiveNamedToolsAreRegistered()
    {
        var tools = await ToolsFor(Contributor(new FakeAgentBrowserProcessRunner()), Context());

        tools.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ShouldBe(
        [
            "browser_click",
            "browser_navigate",
            "browser_screenshot",
            "browser_snapshot",
            "browser_type",
        ]);
    }

    [Fact]
    public async Task Ac2_EveryToolsNameMatchesTheNameInsideItsOwnDefinition()
    {
        // The agent loop routes on IAgentTool.Name but the model sees Definition.Name. A mismatch
        // produces a tool the model can call and the loop cannot find.
        var tools = await ToolsFor(Contributor(new FakeAgentBrowserProcessRunner()), Context());

        foreach (var tool in tools)
        {
            tool.Definition.Name.ShouldBe(tool.Name);
            tool.Definition.Description.ShouldNotBeNullOrWhiteSpace();
        }
    }

    // ---- AC3: schema shape ----------------------------------------------------------------

    [Fact]
    public async Task Ac3_EveryToolSchema_HasATopLevelObjectTypeAndNoRootLevelAnyOf()
    {
        var tools = await ToolsFor(Contributor(new FakeAgentBrowserProcessRunner()), Context());

        tools.Count.ShouldBe(5, "the shape check must cover all five tools, not a subset.");

        foreach (var tool in tools)
        {
            var schema = tool.Definition.Parameters;

            schema.ValueKind.ShouldBe(JsonValueKind.Object, tool.Name);
            schema.TryGetProperty("type", out var type).ShouldBeTrue(tool.Name);
            type.GetString().ShouldBe("object", tool.Name);

            schema.TryGetProperty("anyOf", out _).ShouldBeFalse(
                $"{tool.Name} must not declare a root-level anyOf.");
            schema.TryGetProperty("oneOf", out _).ShouldBeFalse(tool.Name);
            schema.TryGetProperty("allOf", out _).ShouldBeFalse(tool.Name);
        }
    }

    [Fact]
    public async Task Ac3_NoToolSchema_ContainsANestedUnionAnywhere()
    {
        var tools = await ToolsFor(Contributor(new FakeAgentBrowserProcessRunner()), Context());

        foreach (var tool in tools)
        {
            AssertNoUnion(tool.Definition.Parameters, tool.Name);
        }

        static void AssertNoUnion(JsonElement element, string toolName)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                    {
                        property.Name.ShouldNotBeOneOf(["anyOf", "oneOf", "allOf"]);

                        // A "type": ["string","null"] array is a union in the other spelling and
                        // is rejected by the same providers, so it is caught here too.
                        if (property.Name == "type")
                        {
                            property.Value.ValueKind.ShouldBe(JsonValueKind.String, toolName);
                        }

                        AssertNoUnion(property.Value, toolName);
                    }

                    break;

                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                    {
                        AssertNoUnion(item, toolName);
                    }

                    break;
            }
        }
    }

    // ---- AC4: child environment built from empty ------------------------------------------

    [Fact]
    public void Ac4_TheChildEnvironment_IsBuiltFromEmptyAndExcludesAParentSentinelSecret()
    {
        var parent = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [SentinelSecretName] = SentinelSecretValue,
            ["ANTHROPIC_API_KEY"] = "sk-ant-should-never-be-copied",
            ["PATH"] = "/usr/bin",
        };

        var child = AgentBrowserEnvironment.Build(name => parent.GetValueOrDefault(name));

        child.ShouldNotContainKey(SentinelSecretName,
            "the child environment must be built from empty, not inherited (GHSA-m4m8-xjp4-5rmm).");
        child.ShouldNotContainKey("ANTHROPIC_API_KEY");
        child.Values.ShouldNotContain(SentinelSecretValue);
        child["PATH"].ShouldBe("/usr/bin", "the allow-list must still admit what the child needs.");
    }

    [Fact]
    public void Ac4_TheAllowList_ContainsNoNameThatCouldCarryAuthenticationMaterial()
    {
        // A guard on the LIST itself. Without it, AC4's sentinel test keeps passing while someone
        // adds "GITHUB_TOKEN" to the allow-list for convenience.
        string[] forbiddenFragments =
            ["KEY", "TOKEN", "SECRET", "PASSWORD", "CREDENTIAL", "AUTH", "COOKIE"];

        foreach (var name in AgentBrowserEnvironment.AllowedVariables)
        {
            foreach (var fragment in forbiddenFragments)
            {
                name.Contains(fragment, StringComparison.OrdinalIgnoreCase).ShouldBeFalse(
                    $"'{name}' looks like it could carry a credential and must not be allow-listed.");
            }
        }
    }

    [Fact]
    public async Task Ac4_TheEnvironmentActuallyPassedToTheProcess_IsTheAllowListedOne()
    {
        // Building a clean environment and then handing the runner a different one would pass the
        // unit test above and still leak. This asserts the value at the process boundary.
        var runner = new FakeAgentBrowserProcessRunner();
        var tools = await ToolsFor(Contributor(runner), Context());

        await InvokeAsync(Tool(tools, "browser_navigate"), ("url", "https://example.com/"));

        runner.Invocations.ShouldNotBeEmpty();
        foreach (var invocation in runner.Invocations)
        {
            invocation.Environment.ShouldNotContainKey(SentinelSecretName);
            invocation.Environment.Keys.ShouldAllBe(
                k => AgentBrowserEnvironment.AllowedVariables.Contains(k));
        }
    }

    // ---- AC5: timeouts --------------------------------------------------------------------

    [Fact]
    public void Ac5_CommandTimeout_DefaultsToTheConfiguredValue()
    {
        var cli = new AgentBrowserCli(
            "s", Resolved(), new FakeAgentBrowserProcessRunner(),
            new BrowserToolsConfig { CommandTimeoutSeconds = 30 });

        cli.NextTimeoutFor("snapshot").ShouldBe(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Ac5_TheFirstNavigate_GetsAtLeastA120SecondFloor_AndLaterCommandsDoNot()
    {
        var runner = new FakeAgentBrowserProcessRunner();
        var cli = new AgentBrowserCli(
            "s", Resolved(), runner, new BrowserToolsConfig { CommandTimeoutSeconds = 30 });

        cli.NextTimeoutFor("navigate").ShouldBe(TimeSpan.FromSeconds(120),
            "the first navigate pays cold daemon start plus first Chrome launch.");

        await cli.NavigateAsync("https://example.com/");
        runner.Invocations[0].Timeout.ShouldBe(TimeSpan.FromSeconds(120));

        await cli.NavigateAsync("https://example.com/second");
        runner.Invocations[1].Timeout.ShouldBe(TimeSpan.FromSeconds(30),
            "the floor is for the FIRST navigate only; a steady-state hang must not wait 120s.");
    }

    [Fact]
    public async Task Ac5_AConfiguredTimeoutAboveTheFloor_IsNotReducedToIt()
    {
        // The 120s value is a floor, not a cap. An operator who set 300s meant it.
        var runner = new FakeAgentBrowserProcessRunner();
        var cli = new AgentBrowserCli(
            "s", Resolved(), runner, new BrowserToolsConfig { CommandTimeoutSeconds = 300 });

        await cli.NavigateAsync("https://example.com/");

        runner.Invocations[0].Timeout.ShouldBe(TimeSpan.FromSeconds(300));
    }

    [Fact]
    public async Task Ac5_ATimedOutCommand_SurfacesAsAnErrorRatherThanHanging()
    {
        var runner = new FakeAgentBrowserProcessRunner
        {
            DefaultResult = new AgentBrowserProcessResult(-1, "", "", TimedOut: true),
        };

        var tools = await ToolsFor(Contributor(runner), Context());
        var output = await InvokeAsync(Tool(tools, "browser_navigate"), ("url", "https://example.com/"));

        output.ShouldContain("budget", Case.Insensitive);
        output.ShouldContain("terminated", Case.Insensitive);
    }

    // ---- AC6: missing Chrome ---------------------------------------------------------------

    [Fact]
    public async Task Ac6_MissingChrome_ProducesAnActionableErrorNamingTheInstallCommand()
    {
        var runner = new FakeAgentBrowserProcessRunner
        {
            DefaultResult = new AgentBrowserProcessResult(
                1, "", "Error: Failed to launch the browser process! chrome not found"),
        };

        var tools = await ToolsFor(Contributor(runner), Context());
        var output = await InvokeAsync(Tool(tools, "browser_navigate"), ("url", "https://example.com/"));

        output.ShouldContain(
            "agent-browser install",
            Case.Sensitive,
            "the error must name the command that fixes it, not merely report a failure.");
        output.ShouldContain("Chrome", Case.Insensitive);
    }

    [Fact]
    public async Task Ac6_MissingChrome_ReturnsRatherThanHangingOrThrowing()
    {
        // "never a hang" as a bounded assertion: the call completes well inside the test's own
        // budget, and it returns a tool result rather than propagating an exception.
        var runner = new FakeAgentBrowserProcessRunner
        {
            DefaultResult = new AgentBrowserProcessResult(1, "", "chrome is not installed"),
        };

        var tools = await ToolsFor(Contributor(runner), Context());

        var call = InvokeAsync(Tool(tools, "browser_navigate"), ("url", "https://example.com/"));
        var completed = await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(10)));

        completed.ShouldBe(call, "a missing Chrome must fail fast, never hang.");
        (await call).ShouldContain("agent-browser install");
    }

    [Fact]
    public async Task Ac6_AnUnresolvableBinary_IsReportedWithInstallGuidanceAndLaunchesNothing()
    {
        var runner = new FakeAgentBrowserProcessRunner();
        var contributor = Contributor(
            runner,
            new AgentBrowserResolution(
                AgentBrowserSource.NotFound, null,
                "No agent-browser binary was found. " + AgentBrowserBinaryResolver.InstallGuidance));

        var tools = await ToolsFor(contributor, Context());
        var output = await InvokeAsync(Tool(tools, "browser_navigate"), ("url", "https://example.com/"));

        output.ShouldContain("agent-browser");
        runner.Invocations.ShouldBeEmpty("an unresolved binary must not be launched.");
    }

    [Theory]
    [InlineData("Failed to launch the browser process", true)]
    [InlineData("Could not find Chrome (ver. 121)", true)]
    [InlineData("chrome is not installed", true)]
    [InlineData("Navigation timeout of 30000 ms exceeded", false)]
    [InlineData("net::ERR_NAME_NOT_RESOLVED", false)]
    public void Ac6_MissingChromeDetection_IsSpecificRatherThanMatchingEveryFailure(
        string stderr, bool expected)
    {
        // Non-vacuity: a detector that returned true for everything would pass the tests above
        // while telling an agent to install Chrome after an ordinary DNS failure.
        AgentBrowserCli.LooksLikeMissingChrome(stderr).ShouldBe(expected);
    }

    // ---- AC7: session isolation -------------------------------------------------------------

    [Fact]
    public async Task Ac7_TwoDistinctSessionKeys_ProduceTwoDistinctSessionArguments()
    {
        var runner = new FakeAgentBrowserProcessRunner();
        var contributor = Contributor(runner);

        var first = await ToolsFor(contributor, Context("agent-a", "session-1"));
        var second = await ToolsFor(contributor, Context("agent-b", "session-2"));

        await InvokeAsync(Tool(first, "browser_navigate"), ("url", "https://example.com/one"));
        await InvokeAsync(Tool(second, "browser_navigate"), ("url", "https://example.com/two"));

        var sessions = runner.SessionArguments.Distinct(StringComparer.Ordinal).ToList();

        sessions.Count.ShouldBe(2,
            "two agents must never share a browser profile or its cookies.");
        runner.Invocations.ShouldAllBe(i => i.Arguments.Contains("--session"));
    }

    [Fact]
    public async Task Ac7_TheSameAgentInTwoSessions_AlsoGetsTwoDistinctBrowsers()
    {
        // Keying on the agent alone would let two concurrent sessions of ONE agent share a
        // logged-in browser, which is the same hazard with a smaller blast radius.
        var runner = new FakeAgentBrowserProcessRunner();
        var contributor = Contributor(runner);

        var first = await ToolsFor(contributor, Context("agent-a", "session-1"));
        var second = await ToolsFor(contributor, Context("agent-a", "session-2"));

        await InvokeAsync(Tool(first, "browser_navigate"), ("url", "https://example.com/one"));
        await InvokeAsync(Tool(second, "browser_navigate"), ("url", "https://example.com/two"));

        runner.SessionArguments.Distinct(StringComparer.Ordinal).Count().ShouldBe(2);
    }

    [Fact]
    public async Task Ac7_Teardown_IssuesACloseForEachSession()
    {
        var runner = new FakeAgentBrowserProcessRunner();
        var contributor = Contributor(runner);

        var contributionA = await contributor.ContributeAsync(Context("agent-a", "session-1"));
        var contributionB = await contributor.ContributeAsync(Context("agent-b", "session-2"));

        await InvokeAsync(Tool(contributionA.Tools, "browser_navigate"), ("url", "https://example.com/a"));
        await InvokeAsync(Tool(contributionB.Tools, "browser_navigate"), ("url", "https://example.com/b"));

        foreach (var resource in contributionA.ResourcesToDispose!.Concat(contributionB.ResourcesToDispose!))
        {
            await ((IAsyncDisposable)resource).DisposeAsync();
        }

        var closes = runner.Invocations
            .Where(i => i.Arguments.Contains("close"))
            .Select(i => i.Arguments[i.Arguments.ToList().IndexOf("--session") + 1])
            .Distinct(StringComparer.Ordinal)
            .ToList();

        closes.Count.ShouldBe(2, "each session's browser must be closed on teardown, not just one.");
    }

    [Fact]
    public async Task Ac7_Teardown_IsIdempotentAndNeverThrows()
    {
        // Disposal that throws aborts the rest of the handle's dispose chain, so a failing close
        // would leak whatever was queued behind it.
        var runner = new FakeAgentBrowserProcessRunner();
        var contribution = await Contributor(runner).ContributeAsync(Context());

        await InvokeAsync(Tool(contribution.Tools, "browser_navigate"), ("url", "https://example.com/"));

        var disposer = (IAsyncDisposable)contribution.ResourcesToDispose!.Single();
        await disposer.DisposeAsync();
        await Should.NotThrowAsync(async () => await disposer.DisposeAsync());

        runner.Invocations.Count(i => i.Arguments.Contains("close")).ShouldBe(1);
    }

    [Fact]
    public async Task Ac7_TeardownOfAnUnusedContribution_LaunchesNothing()
    {
        var runner = new FakeAgentBrowserProcessRunner();
        var contribution = await Contributor(runner).ContributeAsync(Context());

        await ((IAsyncDisposable)contribution.ResourcesToDispose!.Single()).DisposeAsync();

        runner.Invocations.ShouldBeEmpty(
            "a browser that was never opened must not be closed by starting one.");
    }

    [Fact]
    public void Ac7_TwoLongSessionKeysSharingAPrefix_StillProduceDistinctSessionIds()
    {
        // The id is truncated for the profile directory name. Truncation without a digest would
        // silently merge two agents whose ids share the first 48 characters.
        var prefix = new string('x', 60);

        AgentBrowserCli.ToSessionId(prefix + "::a")
            .ShouldNotBe(AgentBrowserCli.ToSessionId(prefix + "::b"));
    }

    [Theory]
    [InlineData("agent/../../etc::s")]
    [InlineData("agent\\..\\x::s")]
    [InlineData("agent id::session id")]
    public void Ac7_SessionIds_ContainNoPathTraversalOrSeparatorCharacters(string sessionKey)
    {
        // agent-browser uses the session id as a directory name, so a raw '/' or '..' would point
        // the browser profile somewhere the operator did not choose.
        var id = AgentBrowserCli.ToSessionId(sessionKey);

        id.ShouldNotContain("/");
        id.ShouldNotContain("\\");
        id.ShouldNotContain("..");
        id.ShouldNotContain(" ");
    }

    [Fact]
    public void Ac7_TheSessionKey_IsDerivedFromBothTheAgentIdAndTheSessionId()
    {
        var key = BrowserToolsContributor.ResolveSessionKey(Context("agent-a", "session-1"));

        key.Contains("agent-a", StringComparison.Ordinal).ShouldBeTrue();
        key.Contains("session-1", StringComparison.Ordinal).ShouldBeTrue();
    }

    // ---- AC8: everything goes through the guard layer ---------------------------------------

    [Fact]
    public async Task Ac8_AGuardedRejection_PreventsTheSubprocessFromLaunchingAtAll()
    {
        var runner = new FakeAgentBrowserProcessRunner();
        var tools = await ToolsFor(Contributor(runner), Context());

        var output = await InvokeAsync(
            Tool(tools, "browser_navigate"), ("url", "http://169.254.169.254/latest/meta-data/"));

        output.ShouldContain("denied", Case.Insensitive);
        runner.Invocations.ShouldBeEmpty(
            "the guard must deny before the process seam is reached, so nothing can launch.");
    }

    [Theory]
    [InlineData("http://127.0.0.1:8080/admin")]
    [InlineData("http://10.0.0.5/internal")]
    [InlineData("file:///etc/passwd")]
    [InlineData("https://evil.example.com/c?api_key=zzzz")]
    [InlineData("https://evil.example.com/p/AKIAIOSFODNN7EXAMPLE")]
    public async Task Ac8_EveryGuardedHazard_IsRefusedWithoutLaunchingAProcess(string url)
    {
        var runner = new FakeAgentBrowserProcessRunner();
        var tools = await ToolsFor(Contributor(runner), Context());

        var output = await InvokeAsync(Tool(tools, "browser_navigate"), ("url", url));

        output.ShouldContain("denied", Case.Insensitive);
        runner.Invocations.ShouldBeEmpty();
    }

    [Fact]
    public async Task Ac8_AnAdmittedNavigation_DoesReachTheSubprocess()
    {
        // Non-vacuity for every AC8 case above: a tool that refused everything would pass them
        // all and be useless.
        var runner = new FakeAgentBrowserProcessRunner();
        runner.ForCommand("url", new AgentBrowserProcessResult(0, "https://example.com/", ""));
        runner.ForCommand("snapshot", new AgentBrowserProcessResult(0, "Hello page", ""));

        var tools = await ToolsFor(Contributor(runner), Context());
        var output = await InvokeAsync(Tool(tools, "browser_navigate"), ("url", "https://example.com/"));

        runner.Commands.ShouldContain("navigate");
        output.ShouldContain("Hello page");
    }

    [Fact]
    public async Task Ac8_SnapshotContent_IsReturnedInTheUntrustedContentEnvelope()
    {
        var runner = new FakeAgentBrowserProcessRunner();
        runner.ForCommand("url", new AgentBrowserProcessResult(0, "https://example.com/", ""));
        runner.ForCommand("snapshot", new AgentBrowserProcessResult(0, "page body", ""));

        var tools = await ToolsFor(Contributor(runner), Context());
        var output = await InvokeAsync(Tool(tools, "browser_snapshot"));

        output.ShouldContain(UntrustedContentFence.BeginKeyword);
        output.ShouldContain(UntrustedContentFence.EndKeyword);
        output.ShouldContain("page body");
    }

    [Fact]
    public async Task Ac8_APageThatRewritesItsLocationToABlockedAddress_CannotBeReadBack()
    {
        // The post-navigation re-check from #3030, asserted through the TOOL surface: without it
        // a page could pass the guard at navigate time and serve metadata content at snapshot time.
        var runner = new FakeAgentBrowserProcessRunner();
        runner.ForCommand("url", new AgentBrowserProcessResult(
            0, "http://169.254.169.254/latest/meta-data/", ""));
        runner.ForCommand("snapshot", new AgentBrowserProcessResult(0, "SECRET-IAM-CREDENTIALS", ""));

        var tools = await ToolsFor(Contributor(runner), Context());
        var output = await InvokeAsync(Tool(tools, "browser_snapshot"));

        output.ShouldNotContain("SECRET-IAM-CREDENTIALS");
        output.ShouldContain("denied", Case.Insensitive);
    }

    // ---- interaction tools -------------------------------------------------------------------

    [Fact]
    public async Task Click_PassesTheSelectorThroughAsADiscreteArgument()
    {
        var runner = new FakeAgentBrowserProcessRunner();
        var tools = await ToolsFor(Contributor(runner), Context());

        await InvokeAsync(Tool(tools, "browser_click"), ("selector", "button#submit"));

        runner.Commands.ShouldContain("click");
        runner.Invocations.Single().Arguments.ShouldContain("button#submit");
    }

    [Fact]
    public async Task Type_DoesNotEchoTheTypedTextBackIntoTheTranscript()
    {
        // Whatever a model types is the thing least safe to persist verbatim into session history.
        var runner = new FakeAgentBrowserProcessRunner();
        var tools = await ToolsFor(Contributor(runner), Context());

        var output = await InvokeAsync(
            Tool(tools, "browser_type"),
            ("selector", "input[name=q]"),
            ("text", "hunter2-do-not-echo"));

        output.ShouldNotContain("hunter2-do-not-echo");
        runner.Commands.ShouldContain("type");
    }

    [Fact]
    public async Task Type_ForwardsTheSubmitFlagOnlyWhenAsked()
    {
        var runner = new FakeAgentBrowserProcessRunner();
        var tools = await ToolsFor(Contributor(runner), Context());

        await InvokeAsync(Tool(tools, "browser_type"),
            ("selector", "input"), ("text", "a"), ("submit", true));
        await InvokeAsync(Tool(tools, "browser_type"),
            ("selector", "input"), ("text", "a"));

        runner.Invocations[0].Arguments.ShouldContain("--submit");
        runner.Invocations[1].Arguments.ShouldNotContain("--submit");
    }

    [Fact]
    public async Task Screenshot_ReturnsAWorkspaceRelativePathRatherThanAnAbsoluteOne()
    {
        // An absolute path leaks the host's directory layout into the transcript, and is not what
        // the agent's read tool accepts.
        var runner = new FakeAgentBrowserProcessRunner();
        var tools = await ToolsFor(Contributor(runner), Context());

        var output = await InvokeAsync(Tool(tools, "browser_screenshot"));

        output.ShouldContain("tmp/browser/");
        output.ShouldNotContain(WorkspaceRoot);
        runner.Commands.ShouldContain("screenshot");
    }

    [Fact]
    public async Task MissingRequiredArgument_IsReportedAsAToolResultRatherThanCrashingTheLoop()
    {
        var runner = new FakeAgentBrowserProcessRunner();
        var tools = await ToolsFor(Contributor(runner), Context());

        var output = await InvokeAsync(Tool(tools, "browser_click"));

        output.ShouldContain("selector");
        runner.Invocations.ShouldBeEmpty();
    }

    // ---- AC9: no real process, anywhere ------------------------------------------------------

    [Fact]
    public async Task Ac9_ExercisingEveryTool_LaunchesNoRealProcess()
    {
        var runner = new FakeAgentBrowserProcessRunner();
        runner.ForCommand("url", new AgentBrowserProcessResult(0, "https://example.com/", ""));
        runner.ForCommand("snapshot", new AgentBrowserProcessResult(0, "body", ""));

        var tools = await ToolsFor(Contributor(runner), Context());

        await InvokeAsync(Tool(tools, "browser_navigate"), ("url", "https://example.com/"));
        await InvokeAsync(Tool(tools, "browser_snapshot"));
        await InvokeAsync(Tool(tools, "browser_click"), ("selector", "a"));
        await InvokeAsync(Tool(tools, "browser_type"), ("selector", "input"), ("text", "x"));
        await InvokeAsync(Tool(tools, "browser_screenshot"));

        // Every command was observed by the FAKE. If any had escaped to the real runner it would
        // not appear here, and the count would be short.
        runner.Commands.ShouldContain("navigate");
        runner.Commands.ShouldContain("snapshot");
        runner.Commands.ShouldContain("click");
        runner.Commands.ShouldContain("type");
        runner.Commands.ShouldContain("screenshot");
        runner.Invocations.ShouldAllBe(i => i.BinaryPath == "/opt/agent-browser");
    }

    [Fact]
    public void Ac9_TheRealProcessRunner_IsNeverConstructedByAnyTestPath()
    {
        // A structural statement: the CLI's runner is a constructor parameter with no default
        // reachable from these tests, so substitution is not something a test can forget.
        typeof(AgentBrowserCli)
            .GetConstructors()
            .ShouldAllBe(c => c.GetParameters().Any(p => p.ParameterType == typeof(IAgentBrowserProcessRunner)));
    }
}
