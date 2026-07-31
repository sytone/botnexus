namespace BotNexus.Extensions.ExecTool.Tests;

/// <summary>
/// Issue #2407: hardening of the exec boundary's static policy surface.
/// Covers (a) the widened environment blocklist including the token-sequence matcher,
/// (b) wrapper resolution so a future allowlist sees the effective executable rather than a
/// transparent carrier such as <c>proxychains</c> or <c>setsid</c>, and (c) rejection of
/// escaped-newline shell words at a word boundary.
/// </summary>
public class ExecToolCommandPolicyTests
{
    // ---------- (a) widened env blocklist ----------

    [Theory]
    [InlineData("AWS_ACCESS_KEY_ID")]
    [InlineData("AWS_SECRET_ACCESS_KEY")]
    [InlineData("AWS_SESSION_TOKEN")]
    [InlineData("AWS_SHARED_CREDENTIALS_FILE")]
    [InlineData("AWS_CONFIG_FILE")]
    [InlineData("AWS_ROLE_ARN")]
    [InlineData("AWS_WEB_IDENTITY_TOKEN_FILE")]
    [InlineData("AWS_CONTAINER_CREDENTIALS_FULL_URI")]
    [InlineData("aws_container_authorization_token")]
    public void ValidateEnvKey_AwsCredentialFamily_IsRejected(string key)
        => Should.Throw<ArgumentException>(() => ExecTool.ValidateEnvKey(key));

    [Theory]
    [InlineData("BOTNEXUS_VALIDATION_MODE")]
    [InlineData("BOTNEXUS_VALIDATION_LOCAL_FALLBACK")]
    [InlineData("botnexus_anything_at_all")]
    public void ValidateEnvKey_BotNexusNamespace_IsReserved(string key)
        => Should.Throw<ArgumentException>(() => ExecTool.ValidateEnvKey(key));

    [Theory]
    [InlineData("NODE_TLS_DANGEROUSLY_ALLOW")]
    [InlineData("DANGEROUSLY_OMIT_CHECKS")]
    [InlineData("APP_DISABLE_AUTH")]
    [InlineData("APP_DISABLE_CERT_VERIFY")]
    [InlineData("REGISTRY_DISABLE_SIGNATURE_CHECK")]
    [InlineData("FOO_DISABLE_SSL_VERIFY")]
    [InlineData("FOO_DISABLE_TLS")]
    [InlineData("CLIENT_SKIP_AUTH")]
    [InlineData("client_skip_extra_auth")]
    [InlineData("DISABLE_INTERMEDIATE_TOKENS_TLS")]
    public void ValidateEnvKey_SafetyOffTokenSequence_IsRejected(string key)
        => Should.Throw<ArgumentException>(() => ExecTool.ValidateEnvKey(key));

    [Theory]
    [InlineData("MY_APP_SETTING")]
    [InlineData("AWSOME_TOOL_FLAG")]           // AWS_ prefix must not match AWSOME
    [InlineData("BOTNEXUSX_SETTING")]          // BOTNEXUS_ prefix must not match BOTNEXUSX
    [InlineData("AUTH_DISABLE_MODE")]          // DISABLE must come BEFORE AUTH (order-sensitive)
    [InlineData("AUTH_SKIP_COUNT")]            // SKIP must come BEFORE AUTH
    [InlineData("DISABLED_AUTHORITY")]         // token equality, not substring
    [InlineData("SKIPPED_AUTHORS")]
    [InlineData("DISABLE_CACHE")]              // DISABLE alone is fine
    public void ValidateEnvKey_BenignKeys_AreAllowed(string key)
        => Should.NotThrow(() => ExecTool.ValidateEnvKey(key));

    // ---------- (b) wrapper resolution ----------

    [Theory]
    [InlineData("sudo")]
    [InlineData("nohup")]
    [InlineData("setsid")]
    [InlineData("nice")]
    [InlineData("ionice")]
    [InlineData("time")]
    [InlineData("timeout")]
    [InlineData("env")]
    [InlineData("stdbuf")]
    [InlineData("catchsegv")]
    [InlineData("linux32")]
    [InlineData("linux64")]
    [InlineData("numactl")]
    [InlineData("proxychains")]
    [InlineData("proxychains4")]
    [InlineData("setarch")]
    [InlineData("torify")]
    [InlineData("torsocks")]
    [InlineData("unbuffer")]
    [InlineData("xargs")]
    public void ResolveEffectiveExecutable_KnownWrapper_UnwrapsToPayload(string wrapper)
        => ExecTool.ResolveEffectiveExecutable($"{wrapper} curl https://example.com").ShouldBe("curl");

    [Theory]
    [InlineData("sudo -u root curl x", "curl")]
    [InlineData("nice -n 5 curl x", "curl")]
    [InlineData("timeout 5 curl x", "curl")]
    [InlineData("timeout 30s proxychains4 curl x", "curl")]
    [InlineData("env FOO=bar BAZ=qux curl x", "curl")]
    [InlineData("setsid nohup nice ionice torsocks curl x", "curl")]
    [InlineData("/usr/bin/sudo /usr/bin/curl x", "curl")]
    [InlineData("curl https://example.com", "curl")]
    [InlineData("SUDO CURL x", "CURL")]
    public void ResolveEffectiveExecutable_NestedCarriers_ReturnPayload(string command, string expected)
        => ExecTool.ResolveEffectiveExecutable(command).ShouldBe(expected);

    [Fact]
    public void ResolveEffectiveExecutable_WrapperWithNoPayload_ReturnsWrapper()
        => ExecTool.ResolveEffectiveExecutable("sudo").ShouldBe("sudo");

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveEffectiveExecutable_EmptyCommand_ReturnsEmpty(string command)
        => ExecTool.ResolveEffectiveExecutable(command).ShouldBe(string.Empty);

    [Fact]
    public void ResolveEffectiveExecutable_QuotedPayload_IsUnquoted()
        => ExecTool.ResolveEffectiveExecutable("sudo \"my tool\" arg").ShouldBe("my tool");

    // ---------- (c) escaped-newline shell words ----------

    [Theory]
    [InlineData("curl foo\n\\bar")]
    [InlineData("curl foo\r\n\\bar")]
    [InlineData("curl foo\r\\bar")]
    public void ValidateCommandText_EscapedNewlineWord_IsRejected(string command)
        => Should.Throw<ArgumentException>(() => ExecTool.ValidateCommandText(command));

    [Theory]
    [InlineData("curl foo bar")]
    [InlineData("curl foo\nbar")]
    [InlineData("curl foo\\bar")]
    [InlineData("C:\\tools\\curl.exe")]
    public void ValidateCommandText_BenignCommand_IsAllowed(string command)
        => Should.NotThrow(() => ExecTool.ValidateCommandText(command));
}
