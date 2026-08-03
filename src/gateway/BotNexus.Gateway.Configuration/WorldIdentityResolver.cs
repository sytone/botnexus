using BotNexus.Domain;

namespace BotNexus.Gateway.Configuration;

public static class WorldIdentityResolver
{
    private const string DefaultName = "BotNexus Gateway";

    public static WorldIdentity Resolve(PlatformConfig? config)
    {
        var configured = config?.Gateway?.World;
        var fallbackId = Environment.MachineName;

        var id = string.IsNullOrWhiteSpace(configured?.Id) ? fallbackId : configured.Id;
        var name = string.IsNullOrWhiteSpace(configured?.Name) ? DefaultName : configured.Name;

        return new WorldIdentity
        {
            Id = id,
            Name = name,
            Description = configured?.Description,
            Emoji = configured?.Emoji
        };
    }
}
