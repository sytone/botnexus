using System.IO.Abstractions;

namespace BotNexus.Gateway.Configuration;

/// <summary>Identifies which bounded Trailguide contention wait was exhausted.</summary>
public enum TrailguideRefreshFailureReason
{
    /// <summary>A managed file remained locked after all publication attempts.</summary>
    PublicationContention,
    /// <summary>The in-process workspace coordination wait timed out before refresh started.</summary>
    CoordinationTimeout
}

/// <summary>Trailguide refresh exhausted a bounded contention wait.</summary>
public sealed class TrailguideRefreshException : IOException
{
    /// <summary>Initializes a publication-contention failure after five attempts, preserving the previous destination file.</summary>
    /// <param name="destination">The managed file that could not be published.</param>
    /// <param name="innerException">The final sharing or lock violation.</param>
    public TrailguideRefreshException(string destination, IOException innerException)
        : base($"Trailguide refresh could not publish '{destination}' after 5 attempts because it remained locked. The previous file was preserved.", innerException)
    {
        Reason = TrailguideRefreshFailureReason.PublicationContention;
    }

    private TrailguideRefreshException(string workspace)
        : base($"Trailguide refresh coordination wait for '{workspace}' exceeded 1000ms. This caller did not start a refresh; try again later.")
    {
        Reason = TrailguideRefreshFailureReason.CoordinationTimeout;
    }

    /// <summary>The contention boundary that prevented this refresh from completing.</summary>
    public TrailguideRefreshFailureReason Reason { get; }

    internal static TrailguideRefreshException CoordinationTimeout(string workspace) => new(workspace);
}

/// <summary>Publishes a complete sibling instead of truncating a managed workspace file in place.</summary>
internal static class TrailguideFilePublisher
{
    public static void Publish(IFileSystem fileSystem, string destination, string content)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                PublishOnce(fileSystem, destination, content);
                return;
            }
            catch (IOException error) when ((error.HResult & 0xFFFF) is 32 or 33)
            {
                if (attempt == 5)
                    throw new TrailguideRefreshException(destination, error);

                // Only sharing/lock violations are transient. Four waits total 500ms; unrelated
                // IO and permission failures propagate immediately, never disguised as contention.
                Thread.Sleep(50 * attempt);
            }
        }
    }

    private static void PublishOnce(IFileSystem fileSystem, string destination, string content)
    {
        if (fileSystem.File.Exists(destination)
            && string.Equals(fileSystem.File.ReadAllText(destination), content, StringComparison.Ordinal))
            return;

        var sibling = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            // WriteAllText closes the sibling before publication. Never delete the destination:
            // a failed overwrite must leave its previous complete bytes available to readers.
            fileSystem.File.WriteAllText(sibling, content);
            if (fileSystem.File.Exists(destination))
                fileSystem.File.Replace(sibling, destination, destinationBackupFileName: null);
            else
                fileSystem.File.Move(sibling, destination, overwrite: true);
        }
        finally
        {
            fileSystem.File.Delete(sibling);
        }
    }
}
