using System.Reflection;
using System.Runtime.Loader;
using BotNexus.Gateway.Extensions;

namespace BotNexus.Gateway.Tests.Extensions;

/// <summary>
/// Regression guard for issue #2481: <c>GET /api/skills/telemetry</c> returned HTTP 500 with
/// <c>MissingMethodException: SqliteConnectionFactory.Create(String, Int32)</c> in the published
/// container even though the method exists and the IL is byte-identical.
///
/// Root cause is an AssemblyLoadContext type-identity split. Extensions set
/// <c>CopyLocalLockFileAssemblies=true</c> (required, per #2184/#2001), so each extension
/// directory ships a private copy of <c>Microsoft.Data.Sqlite.dll</c> /
/// <c>BotNexus.Persistence.Sqlite.dll</c>. The pre-existing unification rule only unified an
/// assembly that was <em>already loaded</em> in <see cref="AssemblyLoadContext.Default"/> at the
/// instant the extension was loaded. That makes unification TIME-DEPENDENT: an assembly the host
/// ships but has not lazily touched yet (Microsoft.Data.Sqlite is only loaded on first SQLite use)
/// resolves to the extension's private copy, and the resulting <c>SqliteConnection</c> is a
/// different <see cref="Type"/>.
///
/// The fix makes unification depend on what the host SHIPS (its application base directory), not
/// on what the host happens to have loaded yet.
/// </summary>
public class ExtensionAssemblyLoadContextHostDirectoryUnificationTests
{
    /// <summary>
    /// The core defect, expressed without depending on load ordering: an assembly that the host
    /// ships next to itself but has NOT yet loaded into the default context must still unify with
    /// the host. Pre-fix, <c>ShouldUnifyWithHost</c> returned false for every such assembly, so the
    /// extension got a private copy and type identity split.
    /// </summary>
    [Fact]
    public void ShouldUnifyWithHost_returns_true_for_host_shipped_assembly_not_yet_loaded()
    {
        var candidates = HostShippedButNotYetLoadedAssemblyNames();

        // Non-vacuity guard: the process must actually have host-shipped-but-unloaded assemblies,
        // otherwise the assertion loop below would be empty and prove nothing.
        candidates.ShouldNotBeEmpty(
            "the host base directory must contain at least one managed assembly that is not yet " +
            "loaded in the default context for this regression to be meaningful");

        foreach (var name in candidates)
        {
            ExtensionAssemblyLoadContext.ShouldUnifyWithHost(name).ShouldBeTrue(
                $"'{name}' is shipped by the host, so an extension's private copy must not win (#2481)");
        }
    }

    /// <summary>
    /// The specific assemblies named in issue #2481. These are shipped beside the host, so they
    /// must unify regardless of whether the host has lazily loaded them yet.
    /// </summary>
    [Theory]
    [InlineData("Microsoft.Data.Sqlite")]
    [InlineData("BotNexus.Persistence.Sqlite")]
    [InlineData("SQLitePCLRaw.core")]
    public void ShouldUnifyWithHost_returns_true_for_sqlite_closure(string assemblyName)
    {
        // Guard: the assembly really is shipped by the host in this test's base directory.
        File.Exists(Path.Combine(AppContext.BaseDirectory, assemblyName + ".dll")).ShouldBeTrue(
            $"'{assemblyName}.dll' must be present in the host base directory for this test to mean anything");

        ExtensionAssemblyLoadContext.ShouldUnifyWithHost(assemblyName).ShouldBeTrue();
    }

    /// <summary>
    /// Observable end-to-end assertion on the wire: load a real extension directory that contains
    /// a private copy of the SQLite closure through the real <see cref="ExtensionAssemblyLoadContext"/>
    /// and assert the resolved shared types are REFERENCE-IDENTICAL to the host's
    /// <see cref="Type"/> objects and live in <see cref="AssemblyLoadContext.Default"/>.
    /// </summary>
    [Fact]
    public void Extension_context_resolves_shared_sqlite_types_to_the_hosts_type_identity()
    {
        var extensionDirectory = CreateExtensionDirectoryWithPrivateSqliteCopies();

        var entryAssemblyPath = Path.Combine(extensionDirectory, "BotNexus.Persistence.Sqlite.dll");
        var context = new ExtensionAssemblyLoadContext(entryAssemblyPath, isCollectible: false);

        foreach (var assemblyName in new[] { "BotNexus.Persistence.Sqlite", "Microsoft.Data.Sqlite" })
        {
            var loaded = context.LoadFromAssemblyName(new AssemblyName(assemblyName));

            AssemblyLoadContext.GetLoadContext(loaded).ShouldBeSameAs(
                AssemblyLoadContext.Default,
                $"'{assemblyName}' must resolve from the host's default context, not the extension's private copy (#2481)");
        }

        // The exact type from the stack trace in #2481.
        var factoryType = context
            .LoadFromAssemblyName(new AssemblyName("BotNexus.Persistence.Sqlite"))
            .GetType("BotNexus.Persistence.Sqlite.SqliteConnectionFactory", throwOnError: true)!;

        factoryType.ShouldBeSameAs(typeof(BotNexus.Persistence.Sqlite.SqliteConnectionFactory));

        // ...and its Create() return type must be the host's SqliteConnection, which is what the
        // runtime compared when it reported "Method not found".
        var create = factoryType.GetMethod("Create", BindingFlags.Public | BindingFlags.Static)!;
        create.ReturnType.ShouldBeSameAs(typeof(Microsoft.Data.Sqlite.SqliteConnection));
    }

    /// <summary>
    /// Isolation must be preserved: an assembly that the host does NOT ship still loads privately
    /// into the extension context. This is the #2184/#2001 guarantee the fix must not regress.
    /// </summary>
    [Theory]
    [InlineData("SomeRandom.Extension.PrivateDependency")]
    [InlineData("Totally.Fictional.Assembly.That.Is.Not.Loaded")]
    public void ShouldUnifyWithHost_still_false_for_assembly_the_host_does_not_ship(string assemblyName)
    {
        File.Exists(Path.Combine(AppContext.BaseDirectory, assemblyName + ".dll")).ShouldBeFalse();

        ExtensionAssemblyLoadContext.ShouldUnifyWithHost(assemblyName).ShouldBeFalse();
    }

    /// <summary>
    /// Builds a throwaway "extension" directory containing private copies of the SQLite closure,
    /// exactly mirroring what <c>CopyLocalLockFileAssemblies=true</c> produces in the container.
    /// </summary>
    private static string CreateExtensionDirectoryWithPrivateSqliteCopies()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bn2481-ext-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        string[] privateCopies =
        [
            "BotNexus.Persistence.Sqlite.dll",
            "Microsoft.Data.Sqlite.dll",
            "SQLitePCLRaw.core.dll"
        ];

        var copied = 0;
        foreach (var fileName in privateCopies)
        {
            var source = Path.Combine(AppContext.BaseDirectory, fileName);
            if (!File.Exists(source))
                continue;

            File.Copy(source, Path.Combine(directory, fileName), overwrite: true);
            copied++;
        }

        // Non-vacuity guard: if we could not stage the private copies, the scenario under test does
        // not exist and the test must fail loudly rather than pass trivially.
        copied.ShouldBe(privateCopies.Length,
            "every SQLite closure assembly must be staged as a private extension copy to reproduce #2481");

        return directory;
    }

    /// <summary>
    /// Managed assemblies present in the host's base directory that are not yet loaded into the
    /// default context - the exact population that the old time-dependent rule mis-handled.
    /// </summary>
    private static List<string> HostShippedButNotYetLoadedAssemblyNames()
    {
        var loaded = AssemblyLoadContext.Default.Assemblies
            .Select(a => a.GetName().Name)
            .Where(n => !string.IsNullOrEmpty(n))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return Directory.GetFiles(AppContext.BaseDirectory, "*.dll")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .Where(n => !loaded.Contains(n))
            .Where(n => !ExtensionAssemblyLoadContext.IsHostAssembly(n))
            .Where(IsManagedAssembly)
            .Take(25)
            .ToList();
    }

    private static bool IsManagedAssembly(string simpleName)
    {
        try
        {
            AssemblyName.GetAssemblyName(Path.Combine(AppContext.BaseDirectory, simpleName + ".dll"));
            return true;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
    }
}
