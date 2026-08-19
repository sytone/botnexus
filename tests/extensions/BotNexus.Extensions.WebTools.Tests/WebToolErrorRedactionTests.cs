using System.Net;
using System.Text.RegularExpressions;
using BotNexus.Extensions.WebTools.Tests.Helpers;
using BotNexus.Gateway.Abstractions.Security;

namespace BotNexus.Extensions.WebTools.Tests;

/// <summary>
/// Issue #3360 — the web tools' <b>error</b> paths returned raw exception text and a
/// server-controlled <c>ReasonPhrase</c> straight to the model. <c>UntrustedContentSanitizer</c>
/// already guards the <b>success</b> bodies (#2813), but it is a prompt-injection markup filter,
/// not a secret redactor, and it never ran on the error branches at all. Tool output is persisted
/// to the transcript and the transcript is read by the memory indexer, so a credential reflected
/// into a provider error body reaches durable memory through a second door.
///
/// <para>
/// <b>Clause split, and why it is what makes the AC6-style mutation meaningful.</b> The
/// "redacted" tests below assert hostile material is removed; the "preserved" tests assert benign
/// diagnostics survive byte-for-byte. Deleting the redaction call must redden the first group and
/// leave the second green. A suite whose halves move together would be pinning "the tool returns
/// something" rather than "the tool redacts" — and a redactor that returned a constant would pass
/// a strip-only suite while destroying every diagnostic the operator needs.
/// </para>
/// </summary>
[Trait("Category", "Security")]
public class WebToolErrorRedactionTests
{
    // A credential shape deliberately ABSENT from SecretRedactor's built-in dictionary (AC3).
    // Using a real sk-/ghp_ token would leave the test unable to distinguish "the tool consulted
    // the injected redactor" from "some other layer happened to know that prefix".
    private const string SyntheticCredential = "wsk-3360-SYNTHETIC-CREDENTIAL-9f2b";
    private const string InternalUrl = "https://vault.internal.corp.example/v1/secret/web-key";

    // ---------------------------------------------------------------------------------------
    // AC3 — a credential carried in error text does not reach the model.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task WebSearch_HttpRequestExceptionCarryingCredential_IsRedacted()
    {
        var result = await SearchWithThrownAsync(
            new HttpRequestException($"401 Unauthorized (x-api-key: {SyntheticCredential})"),
            new StubRedactor());

        result.ShouldNotContain(SyntheticCredential);
        // Non-vacuity: the diagnostic must still be USEFUL, not blanked. An implementation that
        // replaced the whole message with a constant would pass the line above and be a
        // regression — the operator would lose the status code that names the failure.
        result.ShouldContain("401 Unauthorized");
        result.ShouldContain("[REDACTED]");
    }

    [Fact]
    public async Task WebSearch_GenericExceptionCarryingCredential_IsRedacted()
    {
        // The catch-all branch is the one a future contributor is most likely to add to, and the
        // one that carries the widest variety of upstream text.
        var result = await SearchWithThrownAsync(
            new InvalidOperationException($"provider handshake failed using {SyntheticCredential}"),
            new StubRedactor());

        result.ShouldNotContain(SyntheticCredential);
        result.ShouldContain("handshake failed");
    }

    [Fact]
    public async Task WebSearch_ErrorCarryingInternalUrl_IsRedacted()
    {
        var result = await SearchWithThrownAsync(
            new HttpRequestException($"connection refused to {InternalUrl}"),
            new StubRedactor());

        result.ShouldNotContain(InternalUrl);
        result.ShouldNotContain("vault.internal.corp.example");
        result.ShouldContain("connection refused");
    }

    [Fact]
    public async Task WebFetch_HttpRequestExceptionCarryingCredential_IsRedacted()
    {
        var result = await FetchWithThrownAsync(
            new HttpRequestException($"502 from proxy (Authorization: Bearer {SyntheticCredential})"),
            new StubRedactor());

        result.ShouldNotContain(SyntheticCredential);
        result.ShouldContain("502 from proxy");
    }

    [Fact]
    public async Task WebFetch_GenericExceptionCarryingInternalUrl_IsRedacted()
    {
        var result = await FetchWithThrownAsync(
            new InvalidOperationException($"upstream resolver {InternalUrl} unreachable"),
            new StubRedactor());

        result.ShouldNotContain(InternalUrl);
        result.ShouldContain("unreachable");
    }

    [Fact]
    public async Task WebFetch_NonSuccessStatus_RedactsServerControlledReasonPhrase()
    {
        // Not a catch block: the !IsSuccessStatusCode branch interpolates response.ReasonPhrase,
        // which the SERVER writes, plus the caller-supplied url. A fix that only wrapped the catch
        // branches would leave this path — the one an attacker can actually drive on demand —
        // fully unredacted.
        var handler = new MockHttpMessageHandler();
        handler.SetResponder((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                ReasonPhrase = $"Forbidden - key {SyntheticCredential} rejected",
                Content = new StringContent("nope", System.Text.Encoding.UTF8, "text/plain")
            };
            return Task.FromResult(response);
        });

        var result = await FetchAsync(handler, new StubRedactor());

        result.ShouldNotContain(SyntheticCredential);
        result.ShouldContain("403");
    }

    // ---------------------------------------------------------------------------------------
    // AC4 — a null redactor is a pass-through no-op, not a blanket drop of diagnostics.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task WebSearch_NullRedactor_LeavesMessageUnchanged()
    {
        var result = await SearchWithThrownAsync(
            new HttpRequestException("503 upstream unavailable"),
            secretRedactor: null);

        result.ShouldContain("503 upstream unavailable");
        result.ShouldNotContain("[REDACTED]");
    }

    [Fact]
    public async Task WebFetch_NullRedactor_LeavesMessageUnchanged()
    {
        var result = await FetchWithThrownAsync(
            new HttpRequestException("504 gateway timeout from origin"),
            secretRedactor: null);

        result.ShouldContain("504 gateway timeout from origin");
        result.ShouldNotContain("[REDACTED]");
    }

    // ---------------------------------------------------------------------------------------
    // Non-vacuity — a benign error must survive a REAL (non-null) redactor unchanged.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task WebSearch_BenignErrorWithRedactorAttached_SurvivesUnredacted()
    {
        var result = await SearchWithThrownAsync(
            new HttpRequestException("Name or service not known (DNS failure for example.com)"),
            new StubRedactor());

        result.ShouldContain("Name or service not known");
        result.ShouldContain("DNS failure for example.com");
        result.ShouldNotContain("[REDACTED]");
    }

    [Fact]
    public async Task WebFetch_BenignErrorWithRedactorAttached_SurvivesUnredacted()
    {
        var result = await FetchWithThrownAsync(
            new HttpRequestException("The SSL connection could not be established"),
            new StubRedactor());

        result.ShouldContain("The SSL connection could not be established");
        result.ShouldNotContain("[REDACTED]");
    }

    // ---------------------------------------------------------------------------------------
    // AC2 — structural: a NEW catch branch cannot reach an unredacted message, because no error
    // branch in either tool is permitted to call the raw TextResult sink directly.
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData("WebSearchTool.cs", 3)]
    [InlineData("WebFetchTool.cs", 6)]
    public void ErrorBranches_ReturnThroughTheRedactingSink_NotRawTextResult(string fileName, int minimumCatchBranches)
    {
        var source = ReadToolSource(fileName);
        var catchHeaders = CatchHeader.Matches(source);

        // Guard the fence itself: if the pattern stops finding catch headers the assertions below
        // become vacuously true and the fence silently stops fencing.
        catchHeaders.Count.ShouldBeGreaterThanOrEqualTo(
            minimumCatchBranches,
            $"{fileName} is expected to have at least {minimumCatchBranches} error branches; " +
            "finding fewer means this fence's catch pattern has drifted and is no longer " +
            "inspecting anything.");

        foreach (Match header in catchHeaders)
        {
            var start = header.Index + header.Length;
            var window = source.Substring(start, Math.Min(400, source.Length - start));

            window.ShouldNotContain(
                "return TextResult(",
                Case.Sensitive,
                $"A catch branch in {fileName} returns through the RAW sink. Error text is " +
                "untrusted, server-influenced material; it must return through ErrorResult(), " +
                "the single point where the injected ISecretRedactor is applied (#3360).");
        }
    }

    [Theory]
    [InlineData("WebSearchTool.cs")]
    [InlineData("WebFetchTool.cs")]
    public void RedactingSink_IsDefinedAndIsTheOnlyPlaceTheRedactorIsInvoked(string fileName)
    {
        var source = ReadToolSource(fileName);

        source.ShouldContain(
            "private AgentToolResult ErrorResult(",
            Case.Sensitive,
            $"{fileName} must define the single redacting error sink (#3360).");

        // Exactly one INVOCATION of the redactor in the whole file: the choke point. A second
        // invocation is a second decision, and two places to get redaction right is precisely the
        // defect this issue exists to remove.
        Regex.Matches(source, @"_secretRedactor\s*\.\s*Redact\s*\(").Count.ShouldBe(
            1,
            $"{fileName} should invoke _secretRedactor.Redact exactly once - inside ErrorResult. " +
            "More invocations mean redaction decisions have spread back out to the call sites.");
    }

    private static readonly Regex CatchHeader = new(
        @"catch\s*\([^)]{0,120}\)(?:\s*when\s*\([^)]{0,160}\))?",
        RegexOptions.Compiled);

    private static string ReadToolSource(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;

        dir.ShouldNotBeNull("Could not locate the repository root from the test output directory.");

        var path = Path.Combine(
            dir!.FullName, "src", "extensions", "BotNexus.Extensions.WebTools", fileName);
        File.Exists(path).ShouldBeTrue($"Expected tool source at {path}.");
        return File.ReadAllText(path);
    }

    // ---------------------------------------------------------------------------------------
    // Harness
    // ---------------------------------------------------------------------------------------

    private static async Task<string> SearchWithThrownAsync(Exception thrown, ISecretRedactor? secretRedactor)
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueException(thrown);

        using var tool = new WebSearchTool(
            new WebSearchConfig { Provider = "brave", ApiKey = "token", MaxResults = 5 },
            new HttpClient(handler),
            secretRedactor: secretRedactor);

        var args = await tool.PrepareArgumentsAsync(
            new Dictionary<string, object?> { ["query"] = "botnexus" });
        var result = await tool.ExecuteAsync("call-1", args);
        return result.Content[0].Value!;
    }

    private static async Task<string> FetchWithThrownAsync(Exception thrown, ISecretRedactor? secretRedactor)
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueException(thrown);
        return await FetchAsync(handler, secretRedactor);
    }

    private static async Task<string> FetchAsync(MockHttpMessageHandler handler, ISecretRedactor? secretRedactor)
    {
        using var tool = new WebFetchTool(
            new WebFetchConfig(),
            new HttpClient(handler),
            secretRedactor);

        var args = await tool.PrepareArgumentsAsync(
            new Dictionary<string, object?> { ["url"] = "https://example.com/doc" });
        var result = await tool.ExecuteAsync("call-1", args);
        return result.Content[0].Value!;
    }

    /// <summary>
    /// A redactor that knows only the synthetic shapes these tests inject. It is deliberately NOT
    /// <c>SecretRedactor</c>: using the production dictionary would let a test pass because some
    /// other layer recognised a well-known prefix, rather than because the tool consulted the
    /// redactor it was given. Everything it does not recognise is returned byte-for-byte, which is
    /// what the "benign survives" assertions rely on.
    /// </summary>
    private sealed class StubRedactor : ISecretRedactor
    {
        public string Redact(string input) =>
            string.IsNullOrEmpty(input)
                ? input
                : input
                    .Replace(SyntheticCredential, "[REDACTED]", StringComparison.Ordinal)
                    .Replace(InternalUrl, "[REDACTED]", StringComparison.Ordinal);

        public string RedactForExternalDelivery(string input) => Redact(input);
    }
}
