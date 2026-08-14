namespace BotNexus.Gateway.Abstractions.Audit;

/// <summary>
/// Declares that a method which executes an agent through <c>IAgentHandle</c> is deliberately
/// exempt from the tool-audit sink (issue #2616).
/// </summary>
/// <remarks>
/// <para>
/// The #2616 fence enumerates every <c>IAgentHandle.PromptAsync</c>/<c>StreamAsync</c> call site
/// structurally, from IL, and fails when one does not reach the audit sink. That enumeration is
/// deliberately NOT a list of file names: a name list silently grows a hole every time someone adds
/// a file, which is precisely how the #2127 gap arose. The only sanctioned way to be outside the
/// fence is to say so <b>at the call site</b>, in code, with a reason.
/// </para>
/// <para>
/// Applying this attribute is a security decision, not a way to make a red test green. It is
/// legitimate only when the executed run genuinely cannot produce tool activity that needs a
/// durable record - for example a scripted probe against a stubbed handle. If the run can invoke
/// real tools, route it through the sink instead. A reviewer can find every exemption in the repo
/// with one symbol search, which is the property a file-name allow-list does not have.
/// </para>
/// </remarks>
/// <param name="justification">
/// Why this call site does not need a durable tool-audit record. Must be non-empty; the fence
/// rejects a blank justification so the attribute cannot be used as a silent mute.
/// </param>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Constructor, Inherited = false)]
public sealed class ToolAuditExemptAttribute(string justification) : Attribute
{
    /// <summary>Why this execution call site is outside the tool-audit fence.</summary>
    public string Justification { get; } = justification;
}
