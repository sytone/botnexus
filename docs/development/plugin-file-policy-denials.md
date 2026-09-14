# Plugin file-policy denials

The plugin descriptor fence treats omitted `fileAccess`, explicit JSON `null`, and an empty
policy consistently with respect to the installing ceiling's denials (#3969).

- Ceiling denials always participate, including restrictions inside the agent workspace.
- Omission does not inherit the ceiling's read or write grants. With denials present, the
  effective policy contains those denials and empty grant lists: ordinary neighboring workspace
  paths remain accessible, while denied paths and unrequested external paths do not.
- With no declared policy and no ceiling denials, the effective policy remains null, preserving
  the runtime's workspace-only default.
- Explicit plugin denials are unioned with ceiling denials. Glob patterns retain the runtime
  validator's pattern semantics and denial precedence.
- An ambiguous relative non-glob denial in either source rejects the descriptor with a
  `FileAccess.DeniedPaths` diagnostic, even when the plugin omits its policy. A denial cannot be
  dropped or rebound to another workspace safely without an explicit origin.

## Regression contract

`PluginAgentDescriptorFenceTests.Apply_SerializedPolicy_PreservesCeilingDenials_InRealValidator`
deserializes the actual `PluginAgentDefinition` shape, projects it with `ToDescriptor`, applies
the fence, and exercises the real `DefaultPathValidator`. Omitted, null, empty, and explicit
policies are paired with absolute and glob ceiling denials. Both read and write checks must
reject a protected workspace path, allow its public neighbor, and reject an unrequested external
ceiling grant. Explicit plugin glob denials must survive as well.

`Apply_SerializedPolicy_RejectsAmbiguousCeilingDeny_EvenWhenOmitted` covers the same four shapes
with a relative ceiling denial. `Apply_OmittedPolicy_WithoutCeilingDenials_RetainsWorkspaceOnlyDefault`
pins null and empty ceiling defaults. The existing no-policy assertion checks absence of extra
grants rather than requiring a null representation that would erase restrictions.

Restoring the early null-policy return or conditional ceiling-denial validation must fail the
omitted/null cases. Retain the relative-path, absolute-path, denial-precedence, root-boundary,
and platform-comparison regressions from #3941.

This repair concerns file-policy composition only. Production source registration, serialized
forbidden-member rejection, exhaustive behavioral mutation recipes (#3956), and the advertised
versus bound descriptor-field mismatch (#3966) remain separate obligations of #2685. It does
not widen the permitted descriptor member set or change hosted reconciliation.
