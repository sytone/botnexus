using System.Text.Json.Nodes;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>Owns the shared load, edit tracking, and optimistic save workflow for platform config forms.</summary>
public sealed class PlatformConfigFormModel
{
    private readonly PlatformConfigService _configService;
    private readonly ConfigDirtyPathTracker _dirtyPaths = new();
    private bool _isDirty;

    /// <summary>Top-level schema sections that platform config forms must never persist.</summary>
    public static IReadOnlySet<string> NonPersistedSections { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "$schema", "version", "agents", "extensionRepositories" };

    /// <summary>Creates a form model over the platform config API.</summary>
    public PlatformConfigFormModel(PlatformConfigService configService) => _configService = configService;

    /// <summary>Effective config currently edited by the form.</summary>
    public JsonObject? Config { get; private set; }

    /// <summary>Schema envelope used to render the form.</summary>
    public JsonObject? Schema { get; private set; }

    /// <summary>Revision quoted by the next optimistic save.</summary>
    public string? Revision { get; private set; }

    /// <summary>Whether a load is in progress.</summary>
    public bool IsLoading { get; private set; } = true;

    /// <summary>Whether a save is in progress.</summary>
    public bool IsSaving { get; private set; }

    /// <summary>Whether the form has unsaved edits.</summary>
    public bool IsDirty => _isDirty;

    /// <summary>Loads schema, effective config, and the raw snapshot revision.</summary>
    public async Task LoadAsync()
    {
        IsLoading = true;
        _isDirty = false;
        _dirtyPaths.Reset();
        Schema = await _configService.LoadSchemaAsync();
        Config = await _configService.LoadAsync();
        Revision = (await _configService.LoadSnapshotAsync())?.Revision;
        IsLoading = false;
    }


    /// <summary>Records the current edited config instance.</summary>
    public void MarkConfigChanged(JsonObject config)
    {
        Config = config;
        _isDirty = true;
    }

    /// <summary>Records one edited path for the next atomic patch.</summary>
    public void MarkPathChanged(string path) => _dirtyPaths.Mark(path);

    /// <summary>Saves exactly the edited paths with the revision loaded alongside the form.</summary>
    public async Task<PlatformConfigService.ConfigPatchOutcome> SaveAsync()
    {
        if (Config is null || !IsDirty)
            return new PlatformConfigService.ConfigPatchOutcome(false, false, Revision, "There are no changes to save.");

        IsSaving = true;
        try
        {
            var outcome = await _configService.PatchAsync(_dirtyPaths.BuildOperations(Config), Revision);
            if (outcome.Success)
            {
                _isDirty = false;
                _dirtyPaths.Reset();
                Revision = outcome.Revision;
            }

            return outcome;
        }
        finally
        {
            IsSaving = false;
        }
    }
}
