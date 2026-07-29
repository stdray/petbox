using JetBrains.Annotations;

namespace PetBox.Web.Mcp.Contract;

// PROBE ONLY (resharper-clt-suppression-via-annotations, hypothesis 1) — checking whether
// MeansImplicitUse(WithMembers) on a custom marker actually suppresses
// NotAccessedPositionalProperty.Global on positional record properties, before any real
// rollout. Not wired into the codebase's real doctrine yet.
[MeansImplicitUse(ImplicitUseKindFlags.Access | ImplicitUseKindFlags.Assign, ImplicitUseTargetFlags.WithMembers)]
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class WireContractAttribute : Attribute;
