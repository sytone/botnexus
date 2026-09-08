using System.IO.Abstractions;
using BotNexus.Extensions.Plugins.Security;

namespace BotNexus.Extensions.Skills.Security;

/// <summary>
/// Trust verification mode for skill scripts.
/// </summary>
/// <remarks>
/// Deliberately declared here rather than aliased to <see cref="ContentTrustMode"/>: this is the
/// vocabulary <c>PluginMcpRegistrationFenceArchitectureTests</c> parses out of THIS file to prove
/// the skills, plugin and MCP trust vocabularies have not drifted (#2682). An alias would erase the
/// declaration the fence reads and silently make it vacuous.
/// </remarks>
public enum SkillTrustMode
{
    /// <summary>No trust verification — all skills are allowed (legacy behavior).</summary>
    Disabled,

    /// <summary>Log a warning when trust verification fails, but allow execution.</summary>
    Warn,

    /// <summary>Block execution of skills that fail trust verification.</summary>
    Enforce,
}

/// <summary>
/// Verifies skill script integrity using SHA-256 hash catalogs.
/// Skills with a trust.json catalog are verified; skills without one are
/// treated according to the configured trust mode.
/// </summary>
/// <remarks>
/// <para>
/// <b>Documented forwarding shim (#2682).</b> The hashing, catalog format and verification loop now
/// live in <see cref="ContentTrustCatalog"/> in <c>BotNexus.Extensions.Plugins</c>, which this
/// project already references for plugin skill discovery (#2684). Plugins could not reference this
/// project instead - that edge already runs Skills -> Plugins, so the reverse is a cycle - and
/// giving plugins their own hasher is precisely the drift #2682 exists to prevent. The catalog
/// types are re-exported here so every existing consumer and test keeps compiling unchanged.
/// </para>
/// <para>
/// What this type still owns is the SKILLS POLICY: only <see cref="SkillSecurityScanner.IsScannable"/>
/// files are catalogued, and a file present but absent from the catalog is NOT a violation. Skills
/// legitimately sit beside documentation and assets that were never script content. Plugins take
/// the opposite decision because their catalog covers everything install materialised.
/// </para>
/// </remarks>
public static class SkillTrustVerifier
{
    /// <summary>File name of the trust catalog inside a skill directory.</summary>
    public const string CatalogFileName = ContentTrustCatalog.CatalogFileName;

    /// <summary>
    /// Verifies a skill directory against its trust catalog.
    /// Returns a result indicating whether the skill is trusted.
    /// </summary>
    /// <param name="skillDir">Validated skill directory. Taking a <see cref="SkillPath"/> rather than a
    /// <see cref="string"/> means a caller cannot verify a directory that was never proven to sit
    /// inside a trusted skills root.</param>
    /// <param name="fileSystem">File system abstraction.</param>
    /// <returns>Verification result with any violations found.</returns>
    public static TrustVerificationResult Verify(SkillPath skillDir, IFileSystem? fileSystem = null)
        => ContentTrustCatalog.Verify(
            skillDir.Value,
            fileSystem,
            IsTrustableSkillFile,
            detectUnlistedFiles: false,
            includeDirectory: ContentTrustCatalog.SkipHiddenAndVendorDirectories);

    /// <summary>
    /// Generates a trust catalog for a skill directory by hashing all scannable files.
    /// </summary>
    /// <param name="skillDir">Absolute path to the skill directory.</param>
    /// <param name="fileSystem">File system abstraction.</param>
    /// <returns>A new trust catalog covering all script files in the skill.</returns>
    public static TrustCatalog GenerateCatalog(string skillDir, IFileSystem? fileSystem = null)
        => ContentTrustCatalog.GenerateCatalog(
            skillDir,
            fileSystem,
            IsTrustableSkillFile,
            includeDirectory: ContentTrustCatalog.SkipHiddenAndVendorDirectories);

    /// <summary>
    /// Writes a trust catalog to the skill directory as trust.json.
    /// </summary>
    /// <param name="skillDir">Skill directory to write into.</param>
    /// <param name="catalog">Catalog to persist.</param>
    /// <param name="fileSystem">File system abstraction.</param>
    public static void WriteCatalog(string skillDir, TrustCatalog catalog, IFileSystem? fileSystem = null)
        => ContentTrustCatalog.WriteCatalog(skillDir, catalog, fileSystem);

    /// <summary>Lowercase hex SHA-256 of <paramref name="data"/>.</summary>
    /// <param name="data">Bytes to hash.</param>
    internal static string ComputeSha256(byte[] data) => ContentTrustCatalog.ComputeSha256(data);

    /// <summary>
    /// The skills catalog policy: only files the security scanner would scan. Documentation and
    /// assets are content a skill legitimately ships and are not executable, so hashing them would
    /// turn every README edit into a trust violation.
    /// </summary>
    private static bool IsTrustableSkillFile(string path) => SkillSecurityScanner.IsScannable(path);
}
