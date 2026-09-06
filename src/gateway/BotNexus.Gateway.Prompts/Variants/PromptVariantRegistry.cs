using System.Collections.Frozen;
using System.Reflection;
using System.Text.RegularExpressions;
using BotNexus.Agent.Providers.Core.Registry;

namespace BotNexus.Gateway.Prompts;

/// <summary>
/// The startup-frozen lookup of attribute-declared prompt instruction variants (#2433).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Model adaptation used to be a <c>switch</c> over three hardcoded string
/// arrays inside <see cref="ModelGuidanceSection"/>. It failed OPEN: any family the switch had never
/// heard of received zero guidance, silently. It also had no way to express "same rules, one line
/// different", so the three arrays shared no intent at all.
/// </para>
/// <para>
/// <b>Reflection happens exactly once.</b> <see cref="Freeze"/> scans the supplied assemblies,
/// invokes each annotated member, validates the declarations, and copies the result into
/// <see cref="FrozenDictionary{TKey,TValue}"/>. After that the prompt-build path performs dictionary
/// probes only -- <see cref="ReflectionScans"/> is a counter precisely so a test can PROVE the
/// per-turn cost is not a type scan.
/// </para>
/// <para>
/// <b>Resolution ladder.</b> <c>default</c>, <c>family</c>, opted-in <c>family+major</c>, then
/// <c>family+exact version</c>, applied
/// least-specific first so each rung OVERLAYS the one below it by stable rule id: a variant may add
/// a rule, reword one, or drop one with <see cref="PromptRule.Remove"/>, without restating the rest.
/// A rung declared with <see cref="PromptVariantAttribute.Replace"/> discards everything accumulated
/// beneath it instead.
/// </para>
/// </remarks>
public sealed class PromptVariantRegistry
{
    /// <summary>
    /// The shared token grammar for <see cref="PromptVariantAttribute.Family"/> and
    /// <see cref="PromptVariantAttribute.Version"/>. Identical to the file-suffix grammar so a
    /// family spelled one way in an attribute and another way on disk cannot resolve differently.
    /// </summary>
    private static readonly Regex TokenGrammar = new("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.Compiled);

    private static long _reflectionScans;

    private readonly FrozenDictionary<string, SectionVariants> _sections;

    private PromptVariantRegistry(
        FrozenDictionary<string, SectionVariants> sections,
        IReadOnlyList<PromptVariantDeclaration> declarations)
    {
        _sections = sections;
        Declarations = declarations;
    }

    /// <summary>
    /// The number of members reflected over across the process lifetime. A test asserts this does
    /// not move while prompts are being built, which is the machine-checkable form of #2433's
    /// "reflection must not happen at prompt-build time" constraint.
    /// </summary>
    public static long ReflectionScans => Interlocked.Read(ref _reflectionScans);

    /// <summary>
    /// The registry frozen from the prompts assembly. Built lazily on first touch (which happens at
    /// startup, on the first prompt build of the process) and never rebuilt.
    /// </summary>
    public static PromptVariantRegistry Shared { get; } = Freeze(typeof(PromptVariantRegistry).Assembly);

    /// <summary>The section ids that declare at least one variant.</summary>
    public IReadOnlyCollection<string> SectionIds => _sections.Keys;

    /// <summary>
    /// Every variant declaration this registry was frozen from, in discovery order (#2434).
    /// </summary>
    /// <remarks>
    /// Freezing validates the declarations and then throws the raw shape away, which leaves the
    /// STRUCTURAL properties of the corpus -- does every declared section id belong to a real
    /// section, does an overlay removal target a rule that exists -- unobservable to anything but a
    /// second, drifting copy of the reflection walk written inside the test project. Keeping the
    /// declarations lets the conformance suite reflect over the frozen registry itself, so a rule
    /// renamed in the default rung is caught by the same corpus the prompt path actually uses.
    /// </remarks>
    public IReadOnlyList<PromptVariantDeclaration> Declarations { get; }

    /// <summary>
    /// Scans <paramref name="assemblies"/> for <see cref="PromptVariantAttribute"/> declarations and
    /// freezes them into an immutable lookup.
    /// </summary>
    /// <param name="assemblies">The assemblies to scan.</param>
    /// <returns>The frozen registry.</returns>
    /// <exception cref="InvalidOperationException">
    /// A declaration is malformed: a duplicate <c>(section, family, version)</c> key, a section with
    /// no default rung, a version without a family, a token that violates the shared grammar, an
    /// unparseable version, a blank rule id, a duplicate rule id within one rung, or a
    /// removal-shaped rule on the default rung.
    /// </exception>
    public static PromptVariantRegistry Freeze(params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        return FreezeTypes(assemblies.SelectMany(static assembly => assembly.GetTypes()));
    }

    /// <summary>
    /// Freezes a registry from an explicit set of declaring types.
    /// </summary>
    /// <remarks>
    /// The narrow overload exists so a MALFORMED declaration can be tested. Every rejection clause
    /// needs a bad declaration to exist somewhere, and a bad declaration sitting loose in an assembly
    /// would break every assembly-wide freeze in that assembly -- including the production one.
    /// Scoping the scan to named types keeps each negative case isolated to the test that wants it.
    /// </remarks>
    /// <param name="types">The types to scan for <see cref="PromptVariantAttribute"/> declarations.</param>
    /// <returns>The frozen registry.</returns>
    /// <inheritdoc cref="Freeze(Assembly[])" path="/exception"/>
    public static PromptVariantRegistry FreezeTypes(IEnumerable<Type> types)
    {
        ArgumentNullException.ThrowIfNull(types);

        var declarations = new List<PromptVariantDeclaration>();

        foreach (var type in types)
        {
            foreach (var member in EnumerateCandidateMembers(type))
            {
                Interlocked.Increment(ref _reflectionScans);

                foreach (var attribute in member.GetCustomAttributes<PromptVariantAttribute>(inherit: false))
                {
                    declarations.Add(BuildDeclaration(attribute, member));
                }
            }
        }

        var sections = BuildSections(declarations);

        return new PromptVariantRegistry(sections, [.. declarations]);
    }

    /// <summary>True when <paramref name="sectionId"/> declares any variant.</summary>
    /// <param name="sectionId">The stable section id.</param>
    /// <returns>True when the section has a frozen default rung.</returns>
    public bool HasSection(string sectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionId);
        return _sections.ContainsKey(sectionId);
    }

    /// <summary>
    /// Resolves the instruction lines for <paramref name="sectionId"/> against a model family and id.
    /// </summary>
    /// <param name="sectionId">The stable section id.</param>
    /// <param name="family">
    /// The detected model family, or <see langword="null"/>/<c>unknown</c> for a model whose family
    /// could not be determined. Either way the caller gets the DEFAULT rung, never an empty list --
    /// removing the fail-open is the point of #2433.
    /// </param>
    /// <param name="modelId">
    /// The raw model id, used only to read a version off the family token via
    /// <see cref="ModelFamilyVersion"/>. An id that carries no parseable version simply stops the
    /// ladder one rung early.
    /// </param>
    /// <returns>
    /// The resolved instruction lines, or an empty list when the section declares no variants at all.
    /// </returns>
    public IReadOnlyList<string> Resolve(string sectionId, string? family, string? modelId = null)
    {
        var accumulated = new List<PromptRule>();
        foreach (var rung in ResolveDeclarations(sectionId, family, modelId))
            accumulated = Apply(accumulated, rung);

        return accumulated
            .Where(static rule => rule.Text is not null)
            .Select(static rule => rule.Text!)
            .ToList();
    }

    /// <summary>
    /// Returns the rungs actually applied, in least-specific-first order: default, family,
    /// opted-in major, exact version. Prompt rendering and diagnostics share this selection so
    /// discovery order cannot change the reported ladder. Replace rungs remain in the list;
    /// they discard accumulated rules, not the history of which rungs were applied.
    /// </summary>
    /// <param name="sectionId">The stable section id.</param>
    /// <param name="family">The detected family; null or unknown resolves only the default.</param>
    /// <param name="modelId">The raw id parsed by <see cref="ModelFamilyVersion"/>.</param>
    /// <returns>The ordered declarations, or an empty list for an undeclared section.</returns>
    public IReadOnlyList<PromptVariantDeclaration> ResolveDeclarations(string sectionId, string? family, string? modelId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionId);
        if (!_sections.TryGetValue(sectionId, out var section))
            return [];

        var resolved = new List<PromptVariantDeclaration> { section.Default };
        var normalizedFamily = NormalizeFamily(family);
        // Preserve the existing prerequisite: version rungs are reachable only through a
        // declared family rung. A missing family must not gain new behavior through this API.
        if (normalizedFamily is null || !section.Families.TryGetValue(normalizedFamily, out var familyRung))
            return resolved;

        resolved.Add(familyRung);
        if (ModelFamilyVersion.TryParse(modelId, normalizedFamily, out var version))
        {
            if (section.FamilyMajors.TryGetValue(MajorKey(normalizedFamily, version), out var majorRung))
                resolved.Add(majorRung);
            if (section.FamilyVersions.TryGetValue(VersionKey(normalizedFamily, version), out var exactRung))
                resolved.Add(exactRung);
        }

        return resolved;
    }

    // ---- freezing ----

    private static IEnumerable<MemberInfo> EnumerateCandidateMembers(Type type)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (var method in type.GetMethods(flags).Where(static m => !m.IsSpecialName))
            yield return method;

        foreach (var property in type.GetProperties(flags))
            yield return property;
    }

    private static PromptVariantDeclaration BuildDeclaration(PromptVariantAttribute attribute, MemberInfo member)
    {
        var site = $"{member.DeclaringType?.FullName}.{member.Name}";

        var family = attribute.Family;
        if (family is not null)
        {
            if (!TokenGrammar.IsMatch(family))
                throw new InvalidOperationException(
                    $"[PromptVariant] on {site} declares Family '{family}', which violates the shared token " +
                    "grammar (lowercase alphanumerics, '-' between tokens). The grammar is shared with the " +
                    "prompt-override file suffixes so one spelling cannot mean two things.");
        }

        // Validate only the opt-in spelling here; numeric interpretation stays with the
        // existing ModelFamilyVersion parser below (including its component-size limits).
        if (attribute.MatchMajorVersion &&
            (family is null || string.IsNullOrEmpty(attribute.Version) || !attribute.Version.All(char.IsAsciiDigit)))
            throw new InvalidOperationException(
                $"[PromptVariant] on {site} sets MatchMajorVersion, which requires Family and a major-only " +
                "Version (digits only, e.g. '6', not '6-0' or '6-astra').");

        ModelVersion? version = null;
        if (attribute.Version is not null)
        {
            if (family is null)
                throw new InvalidOperationException(
                    $"[PromptVariant] on {site} declares Version '{attribute.Version}' with no Family. A version " +
                    "is meaningless without the family it versions; the ladder has no family-agnostic version rung.");

            if (!TokenGrammar.IsMatch(attribute.Version))
                throw new InvalidOperationException(
                    $"[PromptVariant] on {site} declares Version '{attribute.Version}', which violates the shared " +
                    "token grammar (lowercase alphanumerics, '-' between tokens).");

            // Parse through ModelFamilyVersion so the declaration and the runtime model id are read
            // by ONE parser (#2374). Synthesising "<family>-<version>" is how the declared token is
            // handed to that parser without adding a second one.
            if (!ModelFamilyVersion.TryParse($"{family}-{attribute.Version}", family, out var parsed))
                throw new InvalidOperationException(
                    $"[PromptVariant] on {site} declares Version '{attribute.Version}', which ModelFamilyVersion " +
                    "cannot parse. Spell it as major or major-minor, e.g. '5' or '4-6'.");

            version = parsed;
        }

        return new PromptVariantDeclaration(attribute.SectionId, family, version, attribute.Replace, InvokeRules(member, site), site)
        {
            MatchMajorVersion = attribute.MatchMajorVersion
        };
    }

    private static IReadOnlyList<PromptRule> InvokeRules(MemberInfo member, string site)
    {
        object? raw;
        switch (member)
        {
            case MethodInfo method when method.GetParameters().Length == 0:
                raw = method.Invoke(null, null);
                break;
            case PropertyInfo property when property.GetIndexParameters().Length == 0 && property.CanRead:
                raw = property.GetValue(null);
                break;
            default:
                throw new InvalidOperationException(
                    $"[PromptVariant] on {site} must annotate a static parameterless method or a static readable " +
                    "property returning IReadOnlyList<PromptRule>.");
        }

        if (raw is not IReadOnlyList<PromptRule> rules)
            throw new InvalidOperationException(
                $"[PromptVariant] on {site} must return IReadOnlyList<PromptRule>; it returned " +
                $"'{raw?.GetType().FullName ?? "null"}'.");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Id))
                throw new InvalidOperationException(
                    $"[PromptVariant] on {site} declares a rule with a blank id. The id is what lets a family " +
                    "overlay a single rule instead of restating the whole set.");

            if (!seen.Add(rule.Id))
                throw new InvalidOperationException(
                    $"[PromptVariant] on {site} declares rule id '{rule.Id}' twice. Overlay is keyed by rule id, " +
                    "so a duplicate within one rung has no defined winner.");
        }

        return rules;
    }

    private static FrozenDictionary<string, SectionVariants> BuildSections(List<PromptVariantDeclaration> declarations)
    {
        var defaults = new Dictionary<string, PromptVariantDeclaration>(StringComparer.OrdinalIgnoreCase);
        var families = new Dictionary<string, Dictionary<string, PromptVariantDeclaration>>(StringComparer.OrdinalIgnoreCase);
        var familyMajors = new Dictionary<string, Dictionary<string, PromptVariantDeclaration>>(StringComparer.OrdinalIgnoreCase);
        var familyVersions = new Dictionary<string, Dictionary<string, PromptVariantDeclaration>>(StringComparer.OrdinalIgnoreCase);

        foreach (var declaration in declarations)
        {
            if (declaration.Family is null)
            {
                if (defaults.TryGetValue(declaration.SectionId, out var existing))
                    throw new InvalidOperationException(
                        $"Duplicate [PromptVariant] default rung for section '{declaration.SectionId}': declared at " +
                        $"{existing.Site} and {declaration.Site}. Each (section, family, version) key must be declared once.");

                if (declaration.Replace)
                    throw new InvalidOperationException(
                        $"[PromptVariant] on {declaration.Site} sets Replace on the DEFAULT rung of section " +
                        $"'{declaration.SectionId}'. There is nothing beneath the default to replace.");

                foreach (var rule in declaration.Rules.Where(static rule => rule.Text is null))
                    throw new InvalidOperationException(
                        $"[PromptVariant] on {declaration.Site} declares removal-shaped rule '{rule.Id}' on the " +
                        $"DEFAULT rung of section '{declaration.SectionId}'. A removal only means something in an overlay.");

                defaults[declaration.SectionId] = declaration;
                continue;
            }

            var bucket = declaration.Version is null ? families : declaration.MatchMajorVersion ? familyMajors : familyVersions;
            var key = declaration.Version is null
                ? declaration.Family
                : declaration.MatchMajorVersion
                    ? MajorKey(declaration.Family, declaration.Version.Value)
                    : VersionKey(declaration.Family, declaration.Version.Value);

            if (!bucket.TryGetValue(declaration.SectionId, out var rungs))
                bucket[declaration.SectionId] = rungs = new Dictionary<string, PromptVariantDeclaration>(StringComparer.OrdinalIgnoreCase);

            if (rungs.TryGetValue(key, out var duplicate))
                throw new InvalidOperationException(
                    $"Duplicate [PromptVariant] rung '{key}' for section '{declaration.SectionId}': declared at " +
                    $"{duplicate.Site} and {declaration.Site}. Each (section, family, version) key must be declared once.");

            rungs[key] = declaration;
        }

        // Every section that declares ANY variant must carry a default rung. This is the clause that
        // structurally removes the old Unknown fail-open: there is no reachable state in which a
        // section resolves to nothing because the model was unrecognised.
        foreach (var sectionId in families.Keys.Concat(familyMajors.Keys).Concat(familyVersions.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!defaults.ContainsKey(sectionId))
                throw new InvalidOperationException(
                    $"Section '{sectionId}' declares family variants but no DEFAULT rung. Declare " +
                    "[PromptVariant(sectionId)] with no Family so an unrecognised model gets conservative " +
                    "guidance rather than nothing.");
        }

        return defaults.ToFrozenDictionary(
            static entry => entry.Key,
            entry => new SectionVariants(
                entry.Value,
                (families.TryGetValue(entry.Key, out var f) ? f : []).ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
                (familyMajors.TryGetValue(entry.Key, out var m) ? m : []).ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
                (familyVersions.TryGetValue(entry.Key, out var v) ? v : []).ToFrozenDictionary(StringComparer.OrdinalIgnoreCase)),
            StringComparer.OrdinalIgnoreCase);
    }

    // ---- resolution helpers ----

    private static List<PromptRule> Apply(List<PromptRule> accumulated, PromptVariantDeclaration rung)
    {
        // Replace is the declared escape hatch: the rung stands alone, nothing beneath it survives.
        if (rung.Replace)
            return [.. rung.Rules];

        var result = new List<PromptRule>(accumulated);

        foreach (var overlay in rung.Rules)
        {
            var index = result.FindIndex(existing => string.Equals(existing.Id, overlay.Id, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                // Reword in place, or mark for removal, KEEPING the inherited position. Overlaying a
                // rule must not silently reorder the instructions around it.
                result[index] = overlay;
            }
            else if (overlay.Text is not null)
            {
                result.Add(overlay);
            }
        }

        return result;
    }

    private static string? NormalizeFamily(string? family)
    {
        if (string.IsNullOrWhiteSpace(family))
            return null;

        var trimmed = family.Trim();
        return string.Equals(trimmed, ModelFamilyDetector.Unknown, StringComparison.OrdinalIgnoreCase) ? null : trimmed;
    }

    private static string VersionKey(string family, ModelVersion version) => $"{family}@{version}";

    private static string MajorKey(string family, ModelVersion version) => $"{family}@{version.Major}.*";

    private sealed record SectionVariants(
        PromptVariantDeclaration Default,
        FrozenDictionary<string, PromptVariantDeclaration> Families,
        FrozenDictionary<string, PromptVariantDeclaration> FamilyMajors,
        FrozenDictionary<string, PromptVariantDeclaration> FamilyVersions);
}
