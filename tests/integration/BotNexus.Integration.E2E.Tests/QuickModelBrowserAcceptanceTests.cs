using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Integration.E2E.Tests.PageObjects;
using Microsoft.Data.Sqlite;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace BotNexus.Integration.E2E.Tests;

/// <summary>
/// Real candidate CLI/Kestrel/portal/SQLite acceptance. The LLM alone is deterministic.
/// Owns its fixture exclusively; missing setup or Chromium fails this mandatory fact, never skips.
/// Run remotely only: this test deliberately launches and restarts a candidate gateway.
/// </summary>
public sealed class QuickModelBrowserAcceptanceTests(ITestOutputHelper output)
{
    private const string Agent = NewUserExperienceFixture.QuickModelAgent;
    private const string ModelA = NewUserExperienceFixture.FirstModel;
    private const string ModelB = NewUserExperienceFixture.SecondModel;
    private static readonly JsonSerializerOptions EvidenceJson = new() { WriteIndented = true };

    [Fact]
    [Trait("Category", "QuickModelBrowserAcceptance")]
    public async Task SaveModel_PopulatedChat_AgreesWithCliSqliteRuntimeAndOwnedRestart_RejectsInvalidEditsAtomically()
    {
        var evidence = EvidenceDirectory();
        output.WriteLine($"Private candidate evidence: {evidence}");
        var fixture = new NewUserExperienceFixture { QuickModelAcceptance = true };
        var observations = new List<object>();
        var passed = false;
        string? failure = null;
        try
        {
            await fixture.InitializeAsync();
            fixture.Succeeded.ShouldBeTrue($"Candidate fixture failed (not skipped): {fixture.Error}");
            File.Exists(Path.Combine(fixture.Home, "config.sqlite")).ShouldBeTrue();
            using var http = new HttpClient { BaseAddress = new Uri(fixture.GatewayBaseUrl), Timeout = TimeSpan.FromSeconds(15) };
            var models = await GetObjectArrayAsync(http, $"/api/agents/{Agent}/models");
            var ids = models.Select(model => RequiredString(model, "modelId")).ToArray();
            ids.ShouldContain(ModelA);
            ids.ShouldContain(ModelB);
            ids.ShouldNotContain(NewUserExperienceFixture.DisallowedModel);
            var registered = await GetObjectArrayAsync(http, "/api/models?provider=integration-mock");
            registered.Select(model => RequiredString(model, "modelId")).ShouldContain(NewUserExperienceFixture.DisallowedModel);
            using var create = await http.PostAsJsonAsync("/api/conversations", new { agentId = Agent, title = "4952 populated chat" });
            create.StatusCode.ShouldBe(HttpStatusCode.Created);
            var conversation = ParseObject(await create.Content.ReadAsStringAsync());
            var conversationId = RequiredString(conversation, "conversationId");
            var route = $"{fixture.GatewayBaseUrl}/agent/{Agent}/conversation/{conversationId}";

            using var playwright = await Playwright.CreateAsync();
            // Deliberately do not use TryLaunchBrowserAsync or any skip-aware helper.
            await using var browser = await PlaywrightBootstrap.LaunchChromiumAsync(playwright);
            await using (var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 1440, Height = 1000 } }))
            {
                await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true });
                var page = await context.NewPageAsync();
                try
                {
                    await page.GotoAsync(route);
                    // Other agents remain rendered but hidden in the multi-panel portal.
                    var panel = new ChatPanelPage(page, Agent).Root;
                    var input = panel.Locator("[data-testid='chat-input']");
                    await input.FillAsync("HELLO_WORLD");
                    await panel.Locator("[data-testid='chat-send']").ClickAsync();
                    await panel.Locator(".message.user").Filter(new() { HasTextString = "HELLO_WORLD" }).First.WaitForAsync();
                    await panel.Locator(".message.assistant").Filter(new() { HasTextString = "Hello" }).First.WaitForAsync(new() { Timeout = 45_000 });
                    await panel.Locator(".streaming-indicator").WaitForAsync(new() { State = WaitForSelectorState.Hidden });
                    var before = await AssertAgreementAsync(fixture, http, ModelA, "before", evidence, observations);
                    var descriptorBefore = await GetObjectAsync(http, $"/api/agents/{Agent}");
                    // Full authored config rows include unmodelled fields, unlike a descriptor roundtrip.
                    before.Rows.ShouldContain(row => row.KeyPath == $"agents.{Agent}.futureOpaque.nested" && row.Value == "[1,null,\"keep\"]");
                    before.Rows.ShouldContain(row => row.KeyPath == $"agents.{Agent}.futureOpaque.flag" && row.Value == "true");
                    before.Rows.ShouldContain(row => row.KeyPath == $"agents.{Agent}.toolTimeoutSeconds" && row.Value == "73");
                    before.Rows.ShouldContain(row => row.KeyPath == $"agents.{Agent}.metadata.authoredProbe.nested");
                    await AssertRejectedPatchesAsync(fixture, http, before, evidence, observations);

                    var dialog = await OpenQuickPanelAsync(panel);
                    var select = dialog.Locator(".config-field[data-field='model'] select.config-select");
                    (await select.InputValueAsync()).ShouldBe(ModelA);
                    await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "01-populated-before.png"), FullPage = true });
                    await select.SelectOptionAsync(ModelB);
                    await dialog.GetByRole(AriaRole.Button, new() { Name = "Save Model", Exact = true }).ClickAsync();
                    var status = dialog.Locator("[role='status']");
                    await status.Filter(new() { HasTextString = $"Saved model {ModelB}" }).WaitForAsync(new() { Timeout = 30_000 });
                    (await dialog.Locator(".config-error").CountAsync()).ShouldBe(0);
                    await dialog.GetByRole(AriaRole.Button, new() { Name = "Save Model", Exact = true })
                        .WaitForAsync(new() { State = WaitForSelectorState.Detached });
                    await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "02-saved.png"), FullPage = true });
                    var saved = await AssertAgreementAsync(fixture, http, ModelB, "saved", evidence, observations);
                    AssertOnlyModelChanged(before, saved);
                    var runtimeAfter = await GetObjectAsync(http, $"/api/agents/{Agent}");
                    var expectedRuntime = descriptorBefore.DeepClone().AsObject();
                    expectedRuntime["modelId"] = ModelB;
                    JsonNode.DeepEquals(expectedRuntime, runtimeAfter).ShouldBeTrue("Only runtime modelId may change");
                    await WriteEvidenceAsync(evidence, "saved-runtime-sibling-comparison", new { expectedRuntime, runtimeAfter });
                    await dialog.Locator("button.config-close-btn[aria-label='Close']").ClickAsync();
                    dialog = await OpenQuickPanelAsync(panel);
                    (await dialog.Locator(".config-field[data-field='model'] select").InputValueAsync()).ShouldBe(ModelB);
                }
                catch
                {
                    // Preserve the original assertion even when a disconnected page cannot screenshot.
                    try
                    {
                        await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "failure.png"), FullPage = true, Timeout = 5_000 });
                    }
                    catch (PlaywrightException screenshotError)
                    {
                        output.WriteLine($"Failure screenshot unavailable: {screenshotError.Message}");
                    }
                    throw;
                }
                finally
                {
                    await context.Tracing.StopAsync(new() { Path = Path.Combine(evidence, "save-trace.zip") });
                }
            }

            await AssertFreshBrowserAsync(browser, route, evidence, "03-fresh-before-restart.png");
            var preRestart = await ReadStoreAsync(fixture.Home);
            var oldPid = fixture.AcceptanceProcessId;
            await fixture.RestartQuickModelGatewayAsync();
            observations.Add(new { phase = "restart", oldPid, newPid = fixture.AcceptanceProcessId, sameHome = fixture.Home, sameData = fixture.AcceptanceData });
            using var freshHttp = new HttpClient { BaseAddress = new Uri(fixture.GatewayBaseUrl), Timeout = TimeSpan.FromSeconds(15) };
            var restarted = await AssertAgreementAsync(fixture, freshHttp, ModelB, "restarted", evidence, observations);
            AssertRowsEqual(preRestart, restarted);
            await AssertFreshBrowserAsync(browser, route, evidence, "04-fresh-after-restart.png");
            passed = true;
        }
        catch (Exception ex)
        {
            failure = ex.ToString();
            throw;
        }
        finally
        {
            try
            {
                await fixture.DisposeAsync();
            }
            catch (Exception ex)
            {
                passed = false;
                failure = $"{failure}\nOwned candidate cleanup failed: {ex}";
                throw;
            }
            finally
            {
                await File.WriteAllTextAsync(Path.Combine(evidence, "acceptance-results.json"), JsonSerializer.Serialize(new
                {
                    passed,
                    failure,
                    fixture.PackVersion,
                    assemblyVersion = fixture.AssemblyVersion.ToString(),
                    runId = Environment.GetEnvironmentVariable("RUN_ID"),
                    attribution = "Bind to sender result.json verified sourceSnapshot; test runsettings commit SHA is not candidate identity.",
                    observations,
                    fixture.Log,
                }, EvidenceJson));
                output.WriteLine($"Acceptance results: {Path.Combine(evidence, "acceptance-results.json")}");
            }
        }
    }

    private static async Task AssertRejectedPatchesAsync(NewUserExperienceFixture fixture, HttpClient http,
        StoreSnapshot before, string evidence, List<object> observations)
    {
        var runtimeBefore = await GetObjectAsync(http, $"/api/agents/{Agent}");
        var cases = new (string Name, object Patch)[]
        {
            ("unknown-model", new { model = "unregistered-4952" }),
            ("null-model", new { model = (string?)null }),
            ("blank-model", new { model = " " }),
            ("null-provider", new { model = ModelB, provider = (string?)null }),
            ("blank-provider", new { model = ModelB, provider = " " }),
            ("agent-disallowed-model", new { model = NewUserExperienceFixture.DisallowedModel }),
            ("conflicting-provider", new { model = ModelB, provider = "conflicting-mock" }),
            ("edit-inherit-conflict", new { model = ModelB, inherit = new[] { "model" } }),
        };
        foreach (var (name, patch) in cases)
        {
            using var response = await http.PatchAsJsonAsync($"/api/agents/{Agent}/configuration", patch);
            var body = await response.Content.ReadAsStringAsync();
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, $"{name}: {body}");
            body.ShouldNotBeNullOrWhiteSpace();
            var after = await ReadStoreAsync(fixture.Home);
            AssertRowsEqual(before, after);
            after.Revision.ShouldBe(before.Revision, $"{name} must not commit a revision");
            JsonNode.DeepEquals(runtimeBefore, await GetObjectAsync(http, $"/api/agents/{Agent}")).ShouldBeTrue(name);
            var cli = await fixture.RunCliAsync("agent", $"show {Agent} --json --target \"{fixture.Home}\"");
            RequiredString(ParseObject(cli.StdOut), "model").ShouldBe(ModelA);
            observations.Add(new { phase = "rejected", name, status = (int)response.StatusCode, body, after.Revision });
        }
        await WriteEvidenceAsync(evidence, "rejected-canonical", before);
    }

    private static async Task<StoreSnapshot> AssertAgreementAsync(NewUserExperienceFixture fixture, HttpClient http,
        string model, string phase, string evidence, List<object> observations)
    {
        // Observe the positive registry reload boundary; no sleeps or inference from save response.
        await TestAwait.EventuallyAsync(async () =>
        {
            var current = await GetObjectAsync(http, $"/api/agents/{Agent}");
            return current["modelId"]?.GetValue<string>() == model;
        }, $"fresh runtime descriptor model {model}", TimeSpan.FromSeconds(30));
        var descriptor = await GetObjectAsync(http, $"/api/agents/{Agent}");
        RequiredString(descriptor, "modelId").ShouldBe(model);
        RequiredString(descriptor, "apiProvider").ShouldBe("integration-mock");
        RequiredString(descriptor, "displayName").ShouldBe("Authored quick model agent");
        RequiredString(descriptor, "description").ShouldBe("Preserve authored description");
        (descriptor["toolIds"]?.AsArray() ?? throw new InvalidOperationException("Missing runtime toolIds"))
            .Select(node => node?.GetValue<string>()).ShouldContain("get_datetime");
        var authored = await GetObjectAsync(http, $"/api/agents/{Agent}/configuration");
        RequiredString(authored, "model").ShouldBe(model);
        RequiredString(authored, "provider").ShouldBe("integration-mock");
        var cliResult = await fixture.RunCliAsync("agent", $"show {Agent} --json --target \"{fixture.Home}\"");
        var cli = ParseObject(cliResult.StdOut);
        RequiredString(cli, "model").ShouldBe(model);
        RequiredString(cli, "provider").ShouldBe("integration-mock");
        RequiredString(cli, "displayName").ShouldBe("Authored quick model agent");
        RequiredString(cli, "description").ShouldBe("Preserve authored description");
        var store = await ReadStoreAsync(fixture.Home);
        var row = store.Rows.Single(entry => entry.Scope == 0 && entry.ScopeId == "" && entry.KeyPath == $"agents.{Agent}.model");
        row.State.ShouldBe(3);
        JsonSerializer.Deserialize<string>(row.Value ?? throw new InvalidOperationException("Missing SQLite model value")).ShouldBe(model);
        await WriteEvidenceAsync(evidence, phase, new { cli, authored, descriptor, store });
        observations.Add(new { phase, model, provider = "integration-mock", store.Revision, processId = fixture.AcceptanceProcessId });
        return store;
    }

    private static void AssertOnlyModelChanged(StoreSnapshot before, StoreSnapshot after)
    {
        var modelPath = $"agents.{Agent}.model";
        after.Rows.Where(row => row.KeyPath != modelPath).ToArray().ShouldBe(before.Rows.Where(row => row.KeyPath != modelPath).ToArray());
        after.Rows.Count.ShouldBe(before.Rows.Count);
        after.Revision.ShouldBeGreaterThan(before.Revision);
    }

    private static void AssertRowsEqual(StoreSnapshot before, StoreSnapshot after) => after.Rows.ToArray().ShouldBe(before.Rows.ToArray());

    private static async Task AssertFreshBrowserAsync(IBrowser browser, string route, string evidence, string screenshot)
    {
        await using var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 1440, Height = 1000 } });
        var page = await context.NewPageAsync();
        await page.GotoAsync(route);
        var panel = new ChatPanelPage(page, Agent).Root;
        // Existing populated chat survives refresh and the owned process restart.
        await panel.Locator(".message.user").Filter(new() { HasTextString = "HELLO_WORLD" }).First.WaitForAsync(new() { Timeout = 30_000 });
        await panel.Locator(".message.assistant").Filter(new() { HasTextString = "Hello" }).First.WaitForAsync(new() { Timeout = 30_000 });
        var dialog = await OpenQuickPanelAsync(panel);
        (await dialog.Locator(".config-field[data-field='model'] select").InputValueAsync()).ShouldBe(ModelB);
        (await dialog.Locator(".config-error").CountAsync()).ShouldBe(0);
        (await dialog.GetByRole(AriaRole.Button, new() { Name = "Save Model", Exact = true }).CountAsync()).ShouldBe(0);
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, screenshot), FullPage = true });
    }

    private static async Task<ILocator> OpenQuickPanelAsync(ILocator panel)
    {
        await panel.Locator("[data-testid='chat-config-btn']").ClickAsync();
        var dialog = panel.Locator(".config-dialog");
        await dialog.Locator(".config-field[data-field='model'] select").WaitForAsync(new() { Timeout = 30_000 });
        return dialog;
    }

    private sealed record StoreRow(int Scope, string ScopeId, string KeyPath, int State, string? Value);
    private sealed record StoreSnapshot(long Revision, IReadOnlyList<StoreRow> Rows);

    private static async Task<StoreSnapshot> ReadStoreAsync(string home)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = Path.Combine(home, "config.sqlite"), Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        await using var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync();
        // One SQLite read transaction keeps revision and complete authored rows at one snapshot.
        using var transaction = connection.BeginTransaction(deferred: true);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT revision FROM config_revision WHERE singleton=1";
        var revision = Convert.ToInt64(await command.ExecuteScalarAsync());
        command.CommandText = "SELECT scope,scope_id,key_path,state,value FROM config_entries ORDER BY scope,scope_id,key_path";
        var rows = new List<StoreRow>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                rows.Add(new(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.IsDBNull(4) ? null : reader.GetString(4)));
        }
        await transaction.CommitAsync();
        return new(revision, rows);
    }

    private static async Task<JsonObject> GetObjectAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, path);
        return ParseObject(await response.Content.ReadAsStringAsync());
    }

    private static async Task<JsonObject[]> GetObjectArrayAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, path);
        return (JsonNode.Parse(await response.Content.ReadAsStringAsync())?.AsArray()
            ?? throw new InvalidOperationException($"Missing JSON array: {path}"))
            .Select(node => node?.AsObject() ?? throw new InvalidOperationException("Null model object")).ToArray();
    }

    private static JsonObject ParseObject(string json) => JsonNode.Parse(json)?.AsObject() ?? throw new InvalidOperationException("Missing JSON object");
    private static string RequiredString(JsonObject value, string name) => value[name]?.GetValue<string>() ?? throw new InvalidOperationException($"Missing {name}");
    private static Task WriteEvidenceAsync(string directory, string phase, object value) => File.WriteAllTextAsync(Path.Combine(directory, phase + ".json"), JsonSerializer.Serialize(value, EvidenceJson));

    private static string EvidenceDirectory()
    {
        var runId = Environment.GetEnvironmentVariable("RUN_ID");
        var root = string.IsNullOrWhiteSpace(runId)
            ? Path.Combine(Path.GetTempPath(), "botnexus-browser-evidence")
            : Path.Combine(Path.DirectorySeparatorChar.ToString(), "work", runId, "artifacts", "browser-4952");
        var directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
