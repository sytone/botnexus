using System.IO.Abstractions.TestingHelpers;
using BotNexus.Gateway.Configuration;
using Shouldly;

namespace BotNexus.Gateway.Configuration.Tests;

/// <summary>
/// Behavioural tests for the file-per-secret store (#3528).
/// </summary>
/// <remarks>
/// The "must not appear" acceptance criteria are asserted against a single distinctive sentinel
/// rather than a generic string. A test that asserts a field is null passes just as happily when the
/// feature is missing entirely; asserting that a value which definitely WAS written is absent from a
/// projection that definitely WAS produced is what makes the negative claim non-vacuous.
/// </remarks>
public sealed class FileSecretStoreTests
{
    /// <summary>
    /// The value written in every leak test. Long and unmistakable so a partial leak - a prefix, a
    /// truncated mask - is still caught by a substring search on any prefix of it.
    /// </summary>
    private const string Sentinel = "SENTINEL-c8f2a71d-DO-NOT-LEAK-THIS-VALUE";

    private const string SecretsDir = @"C:\home\secrets";

    private static (FileSecretStore Store, MockFileSystem Fs) NewStore()
    {
        var fs = new MockFileSystem();
        fs.Directory.CreateDirectory(SecretsDir);
        return (new FileSecretStore(SecretsDir, fs), fs);
    }

    [Fact]
    public void Set_writes_the_raw_value_as_the_file_content_named_by_the_key()
    {
        var (store, fs) = NewStore();

        store.Set("MY_API_KEY", Sentinel);

        // Filename IS the key and content IS the value - no wrapper format, because the documented
        // recovery path is an operator running `cat` on the host.
        var path = Path.Combine(SecretsDir, "MY_API_KEY");
        fs.File.Exists(path).ShouldBeTrue();
        fs.File.ReadAllText(path).ShouldBe(Sentinel);
    }

    [Fact]
    public void List_reports_key_and_metadata_and_never_anything_derived_from_content()
    {
        var (store, _) = NewStore();
        store.Set("alpha", Sentinel);

        var listed = store.List().ShouldHaveSingleItem();

        listed.Key.ShouldBe("alpha");
        listed.SizeBytes.ShouldBe(Sentinel.Length);
        listed.CreatedUtc.ShouldNotBe(default);
        listed.ModifiedUtc.ShouldNotBe(default);

        // AC2: no field of the descriptor may carry the value, a prefix of it, a masked form, or a
        // hash. Serialise the whole record and search it - this catches a leak through ANY member,
        // including one added later, which a per-property assertion would miss.
        var serialised = System.Text.Json.JsonSerializer.Serialize(listed);
        serialised.ShouldNotContain(Sentinel);
        serialised.ShouldNotContain("SENTINEL");
        serialised.ShouldNotContain(Sentinel[..8]);
    }

    [Fact]
    public void Store_exposes_no_member_capable_of_returning_a_secret_value()
    {
        // AC3, at the store level: the absence of a read path is the security property, so it is
        // pinned by reflection rather than left to reviewer vigilance. Any future method returning
        // a bare string (a value) fails this and forces a deliberate decision.
        var valueReturning = typeof(IFileSecretStore)
            .GetMethods()
            .Where(m => m.ReturnType == typeof(string) && m.Name != nameof(IFileSecretStore.SecretsDirectory))
            .Where(m => !m.Name.StartsWith("get_", StringComparison.Ordinal))
            .Select(m => m.Name)
            .ToList();

        valueReturning.ShouldBeEmpty(
            "IFileSecretStore gained a member that returns a string. The write-only contract of " +
            "#3528 depends on there being no read path at all: adding one here is what would make " +
            "a read-value API endpoint possible. If a read path is genuinely required, that is a " +
            "design decision, not an implementation detail.");
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("../escape")]
    [InlineData("..\\escape")]
    [InlineData("../../etc/passwd")]
    [InlineData("sub/key")]
    [InlineData("sub\\key")]
    [InlineData("/etc/passwd")]
    [InlineData(@"C:\Windows\System32\evil")]
    [InlineData(@"\\server\share\evil")]
    [InlineData("")]
    [InlineData("key with space")]
    [InlineData("key$injected")]
    public void Set_rejects_any_key_that_is_not_a_plain_in_charset_file_name(string key)
    {
        var (store, _) = NewStore();

        Should.Throw<InvalidSecretKeyException>(() => store.Set(key, Sentinel));
    }

    [Fact]
    public void A_rejected_traversal_key_writes_nothing_anywhere_on_the_filesystem()
    {
        // Non-vacuity for the traversal guard: proving an exception was thrown is weaker than
        // proving no file appeared. A guard that threw AFTER writing would pass the throw test.
        var fs = new MockFileSystem();
        fs.Directory.CreateDirectory(SecretsDir);
        fs.Directory.CreateDirectory(@"C:\home");
        var store = new FileSecretStore(SecretsDir, fs);

        Should.Throw<InvalidSecretKeyException>(() => store.Set(@"..\escaped", Sentinel));

        var everyFile = fs.AllFiles.ToList();
        everyFile.ShouldBeEmpty();
        foreach (var file in everyFile)
            fs.File.ReadAllText(file).ShouldNotContain(Sentinel);
    }

    [Theory]
    [InlineData("MY_KEY")]
    [InlineData("my.key")]
    [InlineData("my-key")]
    [InlineData("my_key")]
    [InlineData("Key123")]
    [InlineData("a")]
    public void Set_accepts_keys_inside_the_allowlist(string key)
    {
        var (store, _) = NewStore();

        // Positive pin: without this the traversal theory could pass by rejecting everything, which
        // would be a store that cannot store anything.
        Should.NotThrow(() => store.Set(key, Sentinel));
        store.List().Select(s => s.Key).ShouldContain(key);
    }

    [Fact]
    public void A_key_of_exactly_the_maximum_length_is_accepted_and_one_over_is_rejected()
    {
        var (store, _) = NewStore();

        Should.NotThrow(() => store.Set(new string('a', 128), Sentinel));
        Should.Throw<InvalidSecretKeyException>(() => store.Set(new string('a', 129), Sentinel));
    }

    [Fact]
    public void Set_on_an_existing_key_replaces_the_value_wholesale()
    {
        // AC7: overwrite takes the full new value. There is no merge, no read-back, and nothing of
        // the previous value survives - which is what makes "the UI offers no pre-populated value"
        // implementable rather than aspirational.
        var (store, fs) = NewStore();
        store.Set("token", Sentinel);

        store.Set("token", "replacement-value");

        var content = fs.File.ReadAllText(Path.Combine(SecretsDir, "token"));
        content.ShouldBe("replacement-value");
        content.ShouldNotContain(Sentinel);
        store.List().Count.ShouldBe(1);
    }

    [Fact]
    public void Delete_removes_the_file_and_the_key_stops_being_listed()
    {
        // AC9.
        var (store, fs) = NewStore();
        store.Set("doomed", Sentinel);
        store.List().Select(s => s.Key).ShouldContain("doomed");

        store.Delete("doomed").ShouldBeTrue();

        store.List().ShouldBeEmpty();
        fs.File.Exists(Path.Combine(SecretsDir, "doomed")).ShouldBeFalse();
    }

    [Fact]
    public void Delete_of_an_absent_key_reports_false_rather_than_throwing()
    {
        var (store, _) = NewStore();

        store.Delete("never-existed").ShouldBeFalse();
    }

    [Fact]
    public void Delete_rejects_a_traversal_key_instead_of_deleting_outside_the_directory()
    {
        var fs = new MockFileSystem();
        fs.Directory.CreateDirectory(SecretsDir);
        fs.AddFile(@"C:\home\config.json", new MockFileData("{}"));
        var store = new FileSecretStore(SecretsDir, fs);

        Should.Throw<InvalidSecretKeyException>(() => store.Delete(@"..\config.json"));

        fs.File.Exists(@"C:\home\config.json").ShouldBeTrue();
    }

    [Fact]
    public void List_on_a_directory_that_does_not_exist_yet_is_empty_rather_than_a_failure()
    {
        // A fresh install has no secrets directory until the first write. Listing must render an
        // empty section, not an error page.
        var fs = new MockFileSystem();
        var store = new FileSecretStore(SecretsDir, fs);

        store.List().ShouldBeEmpty();
    }

    [Fact]
    public void Set_creates_the_secrets_directory_on_first_write()
    {
        var fs = new MockFileSystem();
        var store = new FileSecretStore(SecretsDir, fs);

        store.Set("first", Sentinel);

        fs.Directory.Exists(SecretsDir).ShouldBeTrue();
    }

    [Fact]
    public void List_skips_a_file_whose_name_could_not_have_been_written_through_the_store()
    {
        // A hand-dropped file with an illegal name is unlistable on purpose: surfacing it would
        // hand the UI a key it can neither overwrite nor delete through the validated write path.
        var fs = new MockFileSystem();
        fs.Directory.CreateDirectory(SecretsDir);
        fs.AddFile(Path.Combine(SecretsDir, "bad name.txt"), new MockFileData(Sentinel));
        var store = new FileSecretStore(SecretsDir, fs);

        store.List().ShouldBeEmpty();
    }

    [Fact]
    public void List_is_ordered_by_key()
    {
        var (store, _) = NewStore();
        store.Set("zulu", Sentinel);
        store.Set("alpha", Sentinel);
        store.Set("mike", Sentinel);

        store.List().Select(s => s.Key).ShouldBe(["alpha", "mike", "zulu"]);
    }
}
