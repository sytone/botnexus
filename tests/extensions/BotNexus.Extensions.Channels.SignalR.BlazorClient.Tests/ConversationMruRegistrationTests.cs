using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// #3064 AC1, "scoped to the circuit". Lifetime is a composition-root fact, not an observable
/// property of the service object, so it is pinned where it is actually decided: the registration
/// in the desktop portal's <c>Program.cs</c>. A <c>Singleton</c> here would share one user's
/// navigation history with every other connected user - a cross-circuit information leak, not
/// merely a wrong lifetime - so this scans the composition root rather than trusting a unit test
/// that could never observe the difference.
/// </summary>
public sealed class ConversationMruRegistrationTests
{
    [Fact]
    public void The_mru_is_registered_scoped_in_the_desktop_composition_root()
    {
        var source = ReadDesktopProgramSource();

        Assert.Contains(
            "AddScoped<IConversationMruService, ConversationMruService>()",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_mru_is_never_registered_as_a_singleton_which_would_share_it_across_circuits()
    {
        var source = ReadDesktopProgramSource();

        Assert.DoesNotContain("AddSingleton<IConversationMruService", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AddTransient<IConversationMruService", source, StringComparison.Ordinal);
    }

    [Fact]
    public void The_concrete_mru_implements_the_abstraction_the_composition_root_registers()
    {
        // Vacuity guard for the source scan above: if the type or interface were renamed, the string
        // match could still pass against a stale file while nothing real was wired.
        Assert.True(typeof(IConversationMruService).IsAssignableFrom(typeof(ConversationMruService)));
    }

    private static string ReadDesktopProgramSource()
    {
        var path = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "extensions",
            "BotNexus.Extensions.Channels.SignalR.BlazorClient",
            "Program.cs");

        Assert.True(File.Exists(path), $"Desktop composition root not found at {path}");
        return File.ReadAllText(path);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "BotNexus.slnx")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate BotNexus.slnx from test base directory.");
    }
}
