using System.IO.Abstractions;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Cli;

/// <summary>
/// Answers whether a BotNexus home has configuration at all, rather than whether it has a
/// <c>config.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// Ten CLI commands guarded on <c>File.Exists(configPath)</c> and refused with "Run botnexus init
/// first". That test was equivalent to "is this home configured?" only while JSON was the sole
/// source. Once <c>botnexus config store enable</c> makes <c>config.db</c> authoritative (#3514),
/// a home can hold a complete configuration and no file - and every one of those commands refuses
/// to run against it (#3823).
/// </para>
/// <para>
/// Verified on a store-only test instance: <c>config get</c>, <c>config set</c>, <c>agent list</c>,
/// <c>locations list</c>, <c>prompt list</c>, <c>session list</c>, <c>doctor</c> and <c>validate</c>
/// all failed with that message while 690 entries sat in the store and the gateway served them
/// correctly.
/// </para>
/// <para>
/// The guards themselves are worth keeping: refusing loudly on a genuinely unconfigured home is
/// better than reporting defaults as if they were the operator's settings. Only the question was
/// wrong, so this replaces the predicate rather than deleting the check.
/// </para>
/// </remarks>
public static class ConfigPresence
{
    /// <summary>
    /// True when either a config file or a populated SQLite store exists for this home.
    /// </summary>
    public static bool Exists(string configPath, IFileSystem? fileSystem = null)
    {
        if (string.IsNullOrWhiteSpace(configPath))
            return false;

        var fs = fileSystem ?? new FileSystem();

        if (fs.File.Exists(configPath))
            return true;

        try
        {
            return fs.File.Exists(ConfigStoreBootstrap.ResolveStorePath(configPath, fs));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// The message shown when a home has neither a config file nor a store.
    /// </summary>
    /// <remarks>
    /// Names both sources so the operator is not sent to <c>botnexus init</c> when the real problem
    /// is a store they expected to be present.
    /// </remarks>
    public static string NotFoundMessage(this string configPath)
        => $"[red]Error:[/] No configuration found for this home. Looked for [dim]{CliText.SafeDisplay(configPath)}[/] " +
           $"and a SQLite store beside it. Run [green]botnexus init[/] first.";
}
