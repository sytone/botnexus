using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using BotNexus.Domain.World;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Cli.Commands;

internal sealed class LocationsCommand
{
    private static readonly string[] ValidTypes = ["filesystem", "api", "mcp-server", "database", "remote-node"];

    public Command Build(Option<bool> verboseOption)
    {
        var command = new Command("locations", "Manage configured locations.");

        var listCommand = new Command("list", "List all registered locations.");
        listCommand.SetHandler(async context =>
        {
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            context.ExitCode = await ExecuteListAsync(verbose, CancellationToken.None);
        });

        var nameArgument = new Argument<string>("name", "Location name.");
        var typeOption = new Option<string>("--type", "Location type: filesystem, api, mcp-server, database, remote-node.")
        {
            IsRequired = true
        };
        var pathOption = new Option<string>("--path", "Filesystem path or primary location path.")
        {
            IsRequired = true
        };
        var endpointOption = new Option<string?>("--endpoint", "Endpoint URL for api/mcp-server/remote-node locations.");
        var connectionStringOption = new Option<string?>("--connection-string", "Connection string for database locations.");
        var descriptionOption = new Option<string?>("--description", "Location description.");

        var addCommand = new Command("add", "Add a location to config.json.")
        {
            nameArgument,
            typeOption,
            pathOption,
            endpointOption,
            connectionStringOption,
            descriptionOption
        };
        addCommand.SetHandler(async context =>
        {
            var name = context.ParseResult.GetValueForArgument(nameArgument);
            var type = context.ParseResult.GetValueForOption(typeOption) ?? "filesystem";
            var path = context.ParseResult.GetValueForOption(pathOption) ?? string.Empty;
            var endpoint = context.ParseResult.GetValueForOption(endpointOption);
            var connectionString = context.ParseResult.GetValueForOption(connectionStringOption);
            var description = context.ParseResult.GetValueForOption(descriptionOption);
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            context.ExitCode = await ExecuteAddAsync(name, type, path, endpoint, connectionString, description, verbose, CancellationToken.None);
        });

        var updatePathOption = new Option<string?>("--path", "Updated path.");
        var updateEndpointOption = new Option<string?>("--endpoint", "Updated endpoint.");
        var updateDescriptionOption = new Option<string?>("--description", "Updated description.");
        var updateCommand = new Command("update", "Update an existing location.")
        {
            nameArgument,
            updatePathOption,
            updateEndpointOption,
            updateDescriptionOption
        };
        updateCommand.SetHandler(async context =>
        {
            var name = context.ParseResult.GetValueForArgument(nameArgument);
            var path = context.ParseResult.GetValueForOption(updatePathOption);
            var endpoint = context.ParseResult.GetValueForOption(updateEndpointOption);
            var description = context.ParseResult.GetValueForOption(updateDescriptionOption);
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            context.ExitCode = await ExecuteUpdateAsync(name, path, endpoint, description, verbose, CancellationToken.None);
        });

        var deleteCommand = new Command("delete", "Delete a location from config.json.")
        {
            nameArgument
        };
        deleteCommand.SetHandler(async context =>
        {
            var name = context.ParseResult.GetValueForArgument(nameArgument);
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            context.ExitCode = await ExecuteDeleteAsync(name, verbose, CancellationToken.None);
        });

        command.AddCommand(listCommand);
        command.AddCommand(addCommand);
        command.AddCommand(updateCommand);
        command.AddCommand(deleteCommand);
        return command;
    }

    public async Task<int> ExecuteListAsync(bool verbose, CancellationToken cancellationToken)
    {
        var config = await LoadConfigRequiredAsync(cancellationToken);
        if (config is null)
            return 1;

        var worldDescriptor = WorldDescriptorBuilder.Build(config, null, null);
        var locations = worldDescriptor.Locations
            .OrderBy(location => location.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var declaredLocations = config.Gateway?.Locations ?? new Dictionary<string, LocationConfig>(StringComparer.OrdinalIgnoreCase);
        if (locations.Length == 0)
        {
            Console.WriteLine("Locations:");
            Console.WriteLine("  (none)");
            return 0;
        }

        Console.WriteLine("Name                       Type         Path/Endpoint                                  Description");
        Console.WriteLine("----                       ----         -------------                                  -----------");
        foreach (var location in locations)
        {
            var path = location.Path ?? "(unset)";
            var description = TryFindDictionaryKey(declaredLocations, location.Name, out var matchedName)
                ? (declaredLocations[matchedName].Description ?? "(declared)")
                : "(auto-derived)";

            Console.WriteLine($"{PadRight(location.Name, 26)} {PadRight(location.Type.Value, 12)} {PadRight(path, 46)} {description}");
        }

        var declaredCount = locations.Count(location => TryFindDictionaryKey(declaredLocations, location.Name, out _));
        var autoDerivedCount = locations.Length - declaredCount;
        Console.WriteLine();
        Console.WriteLine($"{locations.Length} locations ({declaredCount} declared, {autoDerivedCount} auto-derived)");
        if (verbose)
            Console.WriteLine($"Loaded from: {PlatformConfigLoader.DefaultConfigPath}");

        return 0;
    }

    public async Task<int> ExecuteAddAsync(
        string name,
        string type,
        string path,
        string? endpoint,
        string? connectionString,
        string? description,
        bool verbose,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            Console.WriteLine("Error: location name is required.");
            return 1;
        }

        var config = await LoadConfigRequiredAsync(cancellationToken);
        if (config is null)
            return 1;

        var normalizedName = name.Trim();
        var normalizedType = type.Trim().ToLowerInvariant();
        if (!ValidTypes.Contains(normalizedType, StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine($"Error: location type '{type}' is invalid. Valid values: {string.Join(", ", ValidTypes)}.");
            return 1;
        }

        config.Gateway ??= new GatewaySettingsConfig();
        config.Gateway.Locations ??= new Dictionary<string, LocationConfig>(StringComparer.OrdinalIgnoreCase);

        if (ContainsDictionaryKey(config.Gateway.Locations, normalizedName))
        {
            Console.WriteLine($"Error: location '{normalizedName}' already exists.");
            return 1;
        }

        var autoDerivedCollision = WorldDescriptorBuilder
            .Build(config, null, null)
            .Locations
            .Any(location => string.Equals(location.Name, normalizedName, StringComparison.OrdinalIgnoreCase));
        if (autoDerivedCollision)
        {
            Console.WriteLine($"Error: location '{normalizedName}' conflicts with an existing auto-derived location.");
            return 1;
        }

        var locationConfig = new LocationConfig
        {
            Type = normalizedType,
            Path = NullIfWhiteSpace(path),
            Endpoint = NullIfWhiteSpace(endpoint),
            ConnectionString = NullIfWhiteSpace(connectionString),
            Description = NullIfWhiteSpace(description)
        };

        if (!TryValidateLocationConfig(normalizedName, locationConfig, out var validationError))
        {
            Console.WriteLine($"Error: {validationError}");
            return 1;
        }

        if (normalizedType.Equals("filesystem", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(locationConfig.Path)
            && !Directory.Exists(locationConfig.Path)
            && !File.Exists(locationConfig.Path))
        {
            Console.WriteLine($"Warning: filesystem path '{locationConfig.Path}' does not exist.");
        }

        config.Gateway.Locations[normalizedName] = locationConfig;
        var saveCode = await SaveAndValidateAsync(config, verbose, cancellationToken);
        if (saveCode != 0)
            return saveCode;

        Console.WriteLine($"Added location '{normalizedName}'.");
        return 0;
    }

    public async Task<int> ExecuteUpdateAsync(
        string name,
        string? path,
        string? endpoint,
        string? description,
        bool verbose,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            Console.WriteLine("Error: location name is required.");
            return 1;
        }

        var config = await LoadConfigRequiredAsync(cancellationToken);
        if (config is null)
            return 1;

        var declaredLocations = config.Gateway?.Locations;
        if (declaredLocations is null || !TryFindDictionaryKey(declaredLocations, name.Trim(), out var matchedName))
        {
            Console.WriteLine($"Error: location '{name}' was not found in declared gateway.locations.");
            return 1;
        }

        if (path is null && endpoint is null && description is null)
        {
            Console.WriteLine("Error: specify at least one option to update (--path, --endpoint, --description).");
            return 1;
        }

        var existing = declaredLocations[matchedName];
        if (path is not null)
            existing.Path = NullIfWhiteSpace(path);
        if (endpoint is not null)
            existing.Endpoint = NullIfWhiteSpace(endpoint);
        if (description is not null)
            existing.Description = NullIfWhiteSpace(description);

        if (!TryValidateLocationConfig(matchedName, existing, out var validationError))
        {
            Console.WriteLine($"Error: {validationError}");
            return 1;
        }

        var saveCode = await SaveAndValidateAsync(config, verbose, cancellationToken);
        if (saveCode != 0)
            return saveCode;

        Console.WriteLine($"Updated location '{matchedName}'.");
        return 0;
    }

    public async Task<int> ExecuteDeleteAsync(string name, bool verbose, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            Console.WriteLine("Error: location name is required.");
            return 1;
        }

        var config = await LoadConfigRequiredAsync(cancellationToken);
        if (config is null)
            return 1;

        var declaredLocations = config.Gateway?.Locations;
        if (declaredLocations is null || !TryFindDictionaryKey(declaredLocations, name.Trim(), out var matchedName))
        {
            Console.WriteLine($"Error: location '{name}' was not found in declared gateway.locations.");
            return 1;
        }

        var references = FindFileAccessReferences(config, matchedName);
        if (references.Count > 0)
        {
            Console.WriteLine($"Warning: Location '{matchedName}' is referenced by fileAccess policies:");
            foreach (var reference in references)
                Console.WriteLine($"- {reference}");
        }

        declaredLocations.Remove(matchedName);
        var saveCode = await SaveAndValidateAsync(config, verbose, cancellationToken);
        if (saveCode != 0)
            return saveCode;

        Console.WriteLine($"Deleted location '{matchedName}'.");
        return 0;
    }

    private static List<string> FindFileAccessReferences(PlatformConfig config, string locationName)
    {
        var references = new List<string>();

        AddPolicyReferences("gateway.fileAccess", config.Gateway?.FileAccess);
        if (config.Agents is not null)
        {
            foreach (var (agentId, agentConfig) in config.Agents)
                AddPolicyReferences($"agents.{agentId}.fileAccess", agentConfig.FileAccess);
        }

        return references;

        void AddPolicyReferences(string scope, FileAccessPolicyConfig? policy)
        {
            if (policy is null)
                return;

            AddPathReferences($"{scope}.allowedReadPaths", policy.AllowedReadPaths);
            AddPathReferences($"{scope}.allowedWritePaths", policy.AllowedWritePaths);
            AddPathReferences($"{scope}.deniedPaths", policy.DeniedPaths);
        }

        void AddPathReferences(string propertyPath, IReadOnlyList<string>? paths)
        {
            if (paths is null)
                return;

            for (var i = 0; i < paths.Count; i++)
            {
                var path = paths[i];
                if (string.IsNullOrWhiteSpace(path))
                    continue;

                var referencedLocation = ExtractReferencedLocation(path);
                if (referencedLocation is not null
                    && string.Equals(referencedLocation, locationName, StringComparison.OrdinalIgnoreCase))
                {
                    references.Add($"{propertyPath}[{i}] = {path}");
                }
            }
        }
    }

    private static string? ExtractReferencedLocation(string rawPath)
    {
        var value = rawPath.Trim();
        if (!value.StartsWith('@') || value.Length <= 1)
            return null;

        var token = value[1..];
        var separatorIndex = token.IndexOfAny(['/', '\\']);
        return separatorIndex >= 0 ? token[..separatorIndex] : token;
    }

    private static bool TryValidateLocationConfig(string name, LocationConfig locationConfig, out string error)
    {
        var type = string.IsNullOrWhiteSpace(locationConfig.Type)
            ? "filesystem"
            : locationConfig.Type.Trim();

        if (type.Equals("filesystem", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(locationConfig.Path))
            {
                error = $"gateway.locations.{name}.path is required for filesystem locations.";
                return false;
            }

            error = string.Empty;
            return true;
        }

        if (type.Equals("api", StringComparison.OrdinalIgnoreCase)
            || type.Equals("mcp-server", StringComparison.OrdinalIgnoreCase)
            || type.Equals("remote-node", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(locationConfig.Endpoint))
            {
                error = $"gateway.locations.{name}.endpoint is required for {type} locations.";
                return false;
            }

            if (!Uri.TryCreate(locationConfig.Endpoint, UriKind.Absolute, out var endpoint)
                || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
            {
                error = $"gateway.locations.{name}.endpoint must be a valid http or https absolute URL.";
                return false;
            }

            error = string.Empty;
            return true;
        }

        if (type.Equals("database", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(locationConfig.ConnectionString))
            {
                error = $"gateway.locations.{name}.connectionString is required for database locations.";
                return false;
            }

            error = string.Empty;
            return true;
        }

        error = $"gateway.locations.{name}.type must be one of: {string.Join(", ", ValidTypes)}.";
        return false;
    }

    private static async Task<PlatformConfig?> LoadConfigRequiredAsync(CancellationToken cancellationToken)
    {
        var configPath = PlatformConfigLoader.DefaultConfigPath;
        if (!File.Exists(configPath))
        {
            Console.WriteLine($"Error: config file not found at '{configPath}'. Run 'botnexus init' first.");
            return null;
        }

        try
        {
            return await PlatformConfigLoader.LoadAsync(configPath, cancellationToken, validateOnLoad: false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: unable to load config: {ex.Message}");
            return null;
        }
    }

    private static async Task<int> SaveAndValidateAsync(PlatformConfig config, bool verbose, CancellationToken cancellationToken)
    {
        var configPath = PlatformConfigLoader.DefaultConfigPath;
        await WriteConfigAsync(config, configPath, cancellationToken);

        var reloaded = await PlatformConfigLoader.LoadAsync(configPath, cancellationToken, validateOnLoad: false);
        var errors = PlatformConfigLoader.Validate(reloaded);
        if (errors.Count > 0)
        {
            Console.WriteLine("Config validation failed after write:");
            foreach (var error in errors)
                Console.WriteLine($"- {error}");
            return 1;
        }

        if (verbose)
            Console.WriteLine($"Saved config: {configPath}");

        return 0;
    }

    private static async Task WriteConfigAsync(PlatformConfig config, string configPath, CancellationToken cancellationToken)
    {
        PlatformConfigLoader.EnsureConfigDirectory(PlatformConfigLoader.DefaultHomePath);
        await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(config, CreateWriteJsonOptions()), cancellationToken);
    }

    private static bool ContainsDictionaryKey<TKey, TValue>(Dictionary<TKey, TValue> dictionary, TKey key)
        where TKey : notnull
    {
        if (dictionary.ContainsKey(key))
            return true;

        if (key is string stringKey)
            return dictionary.Keys.OfType<string>().Any(k => string.Equals(k, stringKey, StringComparison.OrdinalIgnoreCase));

        return false;
    }

    private static bool TryFindDictionaryKey<TKey, TValue>(Dictionary<TKey, TValue> dictionary, TKey key, out TKey matchedKey)
        where TKey : notnull
    {
        if (dictionary.ContainsKey(key))
        {
            matchedKey = key;
            return true;
        }

        if (key is string stringKey)
        {
            foreach (var existingKey in dictionary.Keys)
            {
                if (existingKey is string existingString
                    && string.Equals(existingString, stringKey, StringComparison.OrdinalIgnoreCase))
                {
                    matchedKey = existingKey;
                    return true;
                }
            }
        }

        matchedKey = default!;
        return false;
    }

    private static string PadRight(string value, int width)
        => value.Length >= width ? value : value.PadRight(width);

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static JsonSerializerOptions CreateWriteJsonOptions() => new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
