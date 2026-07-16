using PetBox.Tasks.Workflow;

namespace PetBox.Tasks.Contract;

// The runtime-derived agent guide to a project's process (tasks_methodology_guide, spec
// artifacts-from-definition): markdown prose + the structured invariants it was derived
// from (the machine-readable form — no markdown re-parsing downstream). `Source` says
// where the effective kinds came from: "presets" (no open instance), "instance" (one open
// instance, or a named instance via the optional `name` param), or "instances" (merged
// open instances). DefinitionVersion is the instance rules revision when source is a
// single instance (null on pure presets / multi-instance merge).
//
// Moved to PetBox.Tasks.Engine (methodology-engine-extraction, slice 2): DB-free, and
// MethodologyGuide.Render (the only producer) lives in this assembly too. Namespace stays
// PetBox.Tasks.Contract — it lives in two assemblies now; PetBox.Tasks references Engine,
// so every existing consumer (ITasksService, TasksTools) resolves it unchanged.
public sealed record MethodologyGuideView(
	string Markdown,
	IReadOnlyList<MethodologyInvariant> Invariants,
	string Source,
	long? DefinitionVersion = null);
