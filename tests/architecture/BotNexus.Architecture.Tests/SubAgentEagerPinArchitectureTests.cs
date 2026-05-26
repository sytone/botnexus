using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness functions enforcing the F-6 contract: a sub-agent's
/// child session is pinned to the parent <see cref="BotNexus.Domain.Primitives.ConversationId"/>
/// strictly on the synchronous <c>SpawnAsync</c> path — never inside a
/// <c>Task.Run(...)</c> or any other fire-and-forget continuation.
/// </summary>
/// <remarks>
/// The original bug: <c>DefaultSubAgentManager.SpawnAsync</c> created the
/// child session, then queued <c>Task.Run(RunSubAgentAsync(...))</c> and
/// returned. The conversation pinning happened inside that background task,
/// so there was a window between <c>SpawnAsync</c> returning and the task
/// scheduling during which the child session existed in the store with
/// <c>ConversationId == null</c> — visible as an orphan to
/// <c>ISessionStore.ListByConversationAsync</c> (F-7), to
/// <c>/api/conversations/{id}/history</c>, and to canvas resolvers.
///
/// These fences make the regression structurally impossible:
/// 1. No <c>Task.Run(...)</c> block in <c>DefaultSubAgentManager.cs</c> may
///    contain a <c>.ConversationId = ...</c> assignment.
/// 2. <c>SubAgentSpawnRequest.InheritedConversationId</c> must remain
///    non-nullable so callers cannot construct a "pinless" request that
///    would degrade silently at runtime.
/// </remarks>
public sealed class SubAgentEagerPinArchitectureTests
{
    /// <summary>
    /// In <c>DefaultSubAgentManager.cs</c>, every <c>.ConversationId =</c>
    /// mutation must lexically precede every <c>Task.Run(</c> invocation.
    /// This catches both the inline regression (<c>Task.Run(... .ConversationId = ...)</c>)
    /// AND the original bug shape (assignment inside a helper method called
    /// from <c>Task.Run(() =&gt; HelperAsync(...))</c>).
    /// </summary>
    [Fact]
    public void NoConversationIdMutation_InsideTaskRun_InSubAgentManager()
    {
        var managerPath = LocateManagerFile();
        var source = File.ReadAllText(managerPath);

        var assignmentIndexes = new System.Text.RegularExpressions.Regex(
            @"\.ConversationId\s*=",
            System.Text.RegularExpressions.RegexOptions.Compiled)
            .Matches(source)
            .Select(match => match.Index)
            .ToArray();

        var taskRunIndexes = new System.Text.RegularExpressions.Regex(
            @"\bTask\.Run\s*\(",
            System.Text.RegularExpressions.RegexOptions.Compiled)
            .Matches(source)
            .Select(match => match.Index)
            .ToArray();

        if (assignmentIndexes.Length == 0 || taskRunIndexes.Length == 0)
            return; // nothing to enforce

        var firstTaskRun = taskRunIndexes.Min();
        var lateAssignments = assignmentIndexes
            .Where(idx => idx > firstTaskRun)
            .Select(idx => LineNumberAt(source, idx))
            .ToArray();

        lateAssignments.ShouldBeEmpty(
            "DefaultSubAgentManager.cs has at least one .ConversationId = " +
            "assignment that occurs AFTER the first Task.Run(...) invocation. " +
            "The F-6 contract requires the child session's ConversationId to be " +
            "pinned eagerly on the synchronous SpawnAsync path BEFORE any " +
            "fire-and-forget Task.Run queues the prompt loop. Otherwise the " +
            "child session exists in the store with ConversationId == null for " +
            "the lifetime of the orphan window and is invisible to " +
            "ISessionStore.ListByConversationAsync (F-7), to " +
            "/api/conversations/{id}/history, and to canvas resolvers.\n" +
            "Late assignments at lines: " + string.Join(", ", lateAssignments) + "\n" +
            "First Task.Run at line: " + LineNumberAt(source, firstTaskRun) + "\n" +
            "File: " + managerPath);
    }

    /// <summary>
    /// <c>SubAgentSpawnRequest.InheritedConversationId</c> must be a
    /// non-nullable, required <see cref="BotNexus.Domain.Primitives.ConversationId"/>.
    /// A nullable or optional shape lets callers construct a request that
    /// would pin to nothing, defeating the eager-pin contract.
    /// </summary>
    [Fact]
    public void SpawnRequest_InheritedConversationId_IsRequiredAndNonNullable()
    {
        var requestPath = LocateSpawnRequestFile();
        var source = File.ReadAllText(requestPath);

        // Match the property declaration. Accept any whitespace, require
        // `required`, require `ConversationId` (no `?`), forbid `string?`.
        var requiredNonNullable = new System.Text.RegularExpressions.Regex(
            @"required\s+ConversationId\s+InheritedConversationId\b",
            System.Text.RegularExpressions.RegexOptions.Compiled);
        var nullableLeak = new System.Text.RegularExpressions.Regex(
            @"InheritedConversationId\s*[^=]*\?\s*[{=;]",
            System.Text.RegularExpressions.RegexOptions.Compiled);

        requiredNonNullable.IsMatch(source).ShouldBeTrue(
            "SubAgentSpawnRequest.InheritedConversationId must be declared as " +
            "`required ConversationId InheritedConversationId` (no nullable, " +
            "no string). This prevents callers from constructing a pinless " +
            "spawn request that would silently orphan the child session.\n" +
            "File: " + requestPath);

        nullableLeak.IsMatch(source).ShouldBeFalse(
            "SubAgentSpawnRequest.InheritedConversationId appears to be nullable. " +
            "The F-6 contract requires it to be non-nullable so the eager-pin path " +
            "in DefaultSubAgentManager.SpawnAsync always has a valid id to assign.\n" +
            "File: " + requestPath);
    }

    private static string LocateManagerFile()
    {
        var srcRoot = FindSourceRoot();
        var path = Path.Combine(srcRoot, "gateway", "BotNexus.Gateway", "Agents", "DefaultSubAgentManager.cs");
        File.Exists(path).ShouldBeTrue("Expected DefaultSubAgentManager.cs at " + path);
        return path;
    }

    private static string LocateSpawnRequestFile()
    {
        var srcRoot = FindSourceRoot();
        var path = Path.Combine(srcRoot, "domain", "BotNexus.Domain", "Gateway", "Models", "SubAgentSpawnRequest.cs");
        File.Exists(path).ShouldBeTrue("Expected SubAgentSpawnRequest.cs at " + path);
        return path;
    }

    private static int LineNumberAt(string source, int charIndex)
    {
        var line = 1;
        for (var i = 0; i < charIndex && i < source.Length; i++)
        {
            if (source[i] == '\n')
                line++;
        }
        return line;
    }

    private static string FindSourceRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "BotNexus.slnx")))
        {
            current = current.Parent;
        }

        current.ShouldNotBeNull("Could not locate repo root from " + AppContext.BaseDirectory);
        var srcRoot = Path.Combine(current.FullName, "src");
        Directory.Exists(srcRoot).ShouldBeTrue("Expected src/ under " + current.FullName);
        return srcRoot;
    }
}
