using System.IO.Abstractions;
using System.Text.Json;

namespace BotNexus.Extensions.Skills.Recording;

/// <summary>
/// Persists proposed-but-not-installed skills, outside every directory skill discovery scans.
/// </summary>
/// <remarks>
/// <para>
/// A draft is unreviewed content assembled from a transcript. The one thing that must never happen
/// is a draft becoming loadable without an operator having ruled on it, so drafts are not hidden
/// inside a skills root — they are written somewhere nothing scans, and the constructor refuses a
/// root that sits inside one. That refusal is deliberate: a misconfiguration here is a silent
/// widening, and the repo has already shipped four features whose only production behaviour was a
/// "does nothing" fallback nobody noticed. Failing loudly at construction is the cheaper end of that.
/// </para>
/// <para>
/// The file name is <c>draft.json</c> rather than <c>SKILL.md</c> for the same reason. Even if a
/// draft directory were somehow handed to <see cref="SkillDiscovery"/>, the scan requires a
/// <c>SKILL.md</c> and would skip it — two independent reasons a draft cannot load, neither
/// depending on the other holding.
/// </para>
/// </remarks>
public sealed class SkillDraftStore
{
    /// <summary>Directory name drafts live under, as a sibling of the agent's skills directory.</summary>
    public const string DraftsDirectoryName = "skill-drafts";

    /// <summary>The persisted file inside a draft directory. Deliberately NOT <c>SKILL.md</c>.</summary>
    public const string DraftFileName = "draft.json";

    private readonly IFileSystem _fs;
    private readonly string _root;

    /// <summary>
    /// Creates a store rooted at <paramref name="root"/>.
    /// </summary>
    /// <param name="root">Directory to hold draft subdirectories.</param>
    /// <param name="skillDirectories">
    /// Every directory skill discovery scans for this agent. The root must not be one of them, nor
    /// live inside one.
    /// </param>
    /// <param name="fileSystem">Filesystem abstraction; defaults to the real filesystem.</param>
    /// <exception cref="ArgumentException">The root sits inside a discovery root.</exception>
    public SkillDraftStore(string root, IEnumerable<string?> skillDirectories, IFileSystem? fileSystem = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _fs = fileSystem ?? new FileSystem();
        _root = root;

        foreach (var skillDir in skillDirectories)
        {
            if (string.IsNullOrWhiteSpace(skillDir))
                continue;

            if (IsInside(root, skillDir))
                throw new ArgumentException(
                    $"Draft root '{root}' is inside the skills directory '{skillDir}'. Drafts must " +
                    "live outside every directory skill discovery scans, or an unreviewed proposal " +
                    "becomes a loadable skill.",
                    nameof(root));
        }
    }

    /// <summary>The directory drafts are written to.</summary>
    public string Root => _root;

    /// <summary>Resolves the conventional draft root beside an agent's skills directory.</summary>
    public static string ResolveRoot(string agentDirectory)
        => Path.Combine(agentDirectory, DraftsDirectoryName);

    /// <summary>Writes a draft, replacing any existing one of the same name.</summary>
    public void Save(SkillDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var dir = Path.Combine(_root, draft.Name);
        _fs.Directory.CreateDirectory(dir);
        _fs.File.WriteAllText(
            Path.Combine(dir, DraftFileName),
            JsonSerializer.Serialize(draft, SkillsExtensionJson.IndentedOptions));
    }

    /// <summary>Reads a draft, or null when there is none by that name (or it cannot be parsed).</summary>
    public SkillDraft? TryLoad(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var path = Path.Combine(_root, name, DraftFileName);
        if (!_fs.File.Exists(path))
            return null;

        try
        {
            return JsonSerializer.Deserialize<SkillDraft>(
                _fs.File.ReadAllText(path), SkillsExtensionJson.Options);
        }
        catch (JsonException)
        {
            // A corrupt draft reads as absent rather than throwing. Nothing downstream can act on
            // half a proposal, and a parse failure here must not take the whole tool call down.
            return null;
        }
    }

    /// <summary>Lists the pending drafts, newest proposal first.</summary>
    public IReadOnlyList<SkillDraft> List()
    {
        if (!_fs.Directory.Exists(_root))
            return [];

        var drafts = new List<SkillDraft>();
        foreach (var dir in _fs.Directory.GetDirectories(_root))
        {
            var draft = TryLoad(Path.GetFileName(dir));
            if (draft is not null)
                drafts.Add(draft);
        }

        return drafts.OrderByDescending(d => d.CreatedAt).ToList();
    }

    /// <summary>Removes a draft. Returns false when there was none.</summary>
    public bool Delete(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        var dir = Path.Combine(_root, name);
        if (!_fs.Directory.Exists(dir))
            return false;

        _fs.Directory.Delete(dir, recursive: true);
        return true;
    }

    /// <summary>
    /// True when <paramref name="candidate"/> is <paramref name="parent"/> or lives beneath it.
    /// </summary>
    /// <remarks>
    /// Compared on normalised full paths with a trailing separator on the parent, so
    /// <c>/a/skills-drafts</c> is correctly NOT considered inside <c>/a/skills</c> — a plain
    /// <c>StartsWith</c> on the undecorated parent would say it is.
    /// </remarks>
    private static bool IsInside(string candidate, string parent)
    {
        var normalisedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        var normalisedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (string.Equals(normalisedParent, normalisedCandidate, comparison))
            return true;

        return normalisedCandidate.StartsWith(
            normalisedParent + Path.DirectorySeparatorChar, comparison);
    }
}
