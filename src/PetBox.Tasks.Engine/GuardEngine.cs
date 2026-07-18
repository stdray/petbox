namespace PetBox.Tasks.Workflow;

// The PURE half of the methodology enforcement (methodology-engine-extraction, slice 3). Every
// guard and resolver here used to live in TasksService with its own DB reads spliced into the
// middle of its judgement; each was cut in two — the FETCH stayed in the service (which now does
// it once, into MethodologyEngineContext), the JUDGEMENT moved here. Zero IO, zero linq2db: the
// service maps PlanNode -> NodeState (condition 4) at the boundary.
//
// The verdict contract meets the service's EXCEPTION seam at exactly one point: Decide runs the
// stages in the historical order and STOPS at the first refusal, and the service re-raises that
// verdict as the same exception type/message/ForNode key the stage used to throw itself. The
// partial-mode retry loop therefore sees precisely what it always saw — one refusal per pass,
// naming one node — and its cascade/termination/atomic-propagation are untouched.
public static class GuardEngine
{
	// A NodeId is a 32-hex Guid ("N"); a slug starts [a-z] and can't be 32 hex chars in
	// practice. The single definition — NodeRefResolver.LooksLikeNodeId delegates here, so the
	// engine and the service can never drift on what counts as a NodeId.
	public static bool LooksLikeNodeId(string v) => v.Length == 32 && v.All(Uri.IsHexDigit);

	// Does this kind gate ANY transition on a comment artifact? The engine DECLARING the need
	// is the inversion of the old "read comments in the middle of the decision" (04-doc, seam 1):
	// the service asks this before the write and prefetches the board's comment tags once, only
	// for a kind that can actually gate. A superset answer is fine and safe — it only ever
	// decides whether to pay for a read, never a verdict.
	public static bool NeedsCommentTags(MethodologyRuntime runtime, string? kindSlug) =>
		runtime.Types(kindSlug).Any(w => w.Transitions.Any(t => t.PreconditionArtifact is not null));

	// ONE decision per pass of the upsert's retry loop: resolve the links, then judge, in the
	// order of indictment when a batch violates several rules at once (ResolveLinks ->
	// RequireDefinitionLinks -> ValidateLinkTargets -> RequireBlockers ->
	// RequirePreconditionArtifacts). specRef/ideaRef are GONE — every link (structural or
	// provenance) now arrives through the generic `links:{kind:ref[]}` door plus the blockedBy
	// sugar, and is resolved by its declared Direction (spec methodology-link-kinds-declared).
	public static MethodologyEngineDecision Decide(
		MethodologyEngineContext ctx,
		IReadOnlyList<NodeState> desired,
		IReadOnlyDictionary<string, NodeState> prior,
		IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> linksRaw,
		IReadOnlyDictionary<string, string> blockedByRaw)
	{
		if (ResolveLinks(ctx, desired, prior, linksRaw, blockedByRaw, out var links) is { } v1)
			return MethodologyEngineDecision.Refused(v1);
		if (RequireDefinitionLinks(ctx, desired, prior, links) is { } v2)
			return MethodologyEngineDecision.Refused(v2);
		if (ValidateLinkTargets(ctx, links) is { } v3)
			return MethodologyEngineDecision.Refused(v3);
		if (RequireBlockers(ctx, desired, links) is { } v4)
			return MethodologyEngineDecision.Refused(v4);
		if (RequirePreconditionArtifacts(ctx, desired, prior) is { } v5)
			return MethodologyEngineDecision.Refused(v5);
		return new MethodologyEngineDecision([], links);
	}

	static MethodologyVerdict Bad(string node, string message) => new(node, message, VerdictKind.InvalidArgument);

	// The one link resolver (spec methodology-link-kinds-declared): the blockedBy sugar plus the
	// generic `links:{kind:ref[]}` door, each edge resolved to a real target NodeId with its stored
	// orientation. Two strategies:
	//   • `blocks` (direction-less builtin, via blockedBy sugar OR links.blocks): the blocker's
	//     slug resolves on THIS board over prior overlaid with the batch (a blocker created in the
	//     same call resolves too); the writer is the edge's TO (blocker -> task).
	//   • a directed kind (idea_spec/task_spec/issue_task or a project-declared kind with a
	//     Direction): the writer occupies the END whose kind equals its own; the TARGET is the
	//     opposite end, resolved by SLUG against the boards of that end's kind IN THE ACTIVE
	//     METHODOLOGY-INSTANCE BUCKET (never across instances), NodeId-shaped values passing
	//     through. The orientation follows which end the writer sits on.
	// A node that sets `blocks` both ways (blockedBy and links.blocks) is refused — one link, one way.
	static MethodologyVerdict? ResolveLinks(
		MethodologyEngineContext ctx, IReadOnlyList<NodeState> desired, IReadOnlyDictionary<string, NodeState> prior,
		IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> linksRaw,
		IReadOnlyDictionary<string, string> blockedByRaw,
		out List<ResolvedLink> resolved)
	{
		resolved = [];

		// Same-board slug->id (prior overlaid with the batch) for the direction-less `blocks` kind.
		var slugToId = prior.Values.Where(n => n.NodeId.Length > 0)
			.ToDictionary(n => n.Key, n => n.NodeId, StringComparer.Ordinal);
		foreach (var n in desired.Where(n => n.NodeId.Length > 0))
			slugToId[n.Key] = n.NodeId;

		var blocksSetBy = new HashSet<string>(StringComparer.Ordinal); // double-spec guard

		foreach (var (key, val) in blockedByRaw)
		{
			if (ResolveSameBoard(ctx, slugToId, key, "blockedBy", val, out var id) is { } v) return v;
			resolved.Add(new ResolvedLink("blocks", key, id, WriterIsFrom: false));
			blocksSetBy.Add(key);
		}

		foreach (var (key, byKind) in linksRaw)
		{
			foreach (var (kindRaw, refs) in byKind)
			{
				if (refs.Count == 0) continue;
				var kind = (kindRaw ?? "").Trim().ToLowerInvariant();
				if (kind.Length == 0) continue;
				if (string.Equals(kind, "blocks", StringComparison.OrdinalIgnoreCase))
				{
					if (!blocksSetBy.Add(key))
						return Bad(key, $"node '{key}' sets the `blocks` link two ways (blockedBy and links.blocks) — provide it one way");
					foreach (var r in refs)
					{
						if (ResolveSameBoard(ctx, slugToId, key, "links.blocks", r, out var id) is { } v) return v;
						resolved.Add(new ResolvedLink("blocks", key, id, WriterIsFrom: false));
					}
					continue;
				}
				if (ResolveDirected(ctx, key, kind, refs, resolved) is { } vd) return vd;
			}
		}
		return null;
	}

	// A slug|NodeId that resolves on THIS board (the `blocks` sugar's home). NodeId passes through.
	static MethodologyVerdict? ResolveSameBoard(
		MethodologyEngineContext ctx, Dictionary<string, string> slugToId,
		string key, string field, string val, out string id)
	{
		id = "";
		var v = val.Trim();
		if (LooksLikeNodeId(v)) { id = v; return null; }
		if (!slugToId.TryGetValue(v.ToLowerInvariant(), out var found))
			return Bad(key, $"{field} '{val}' (node '{key}') does not match any node on board '{ctx.Board}' — a blocker's slug resolves on the same board; pass a NodeId to reference a node on another board");
		id = found;
		return null;
	}

	// Resolve a DIRECTED link kind's refs. The writer node (kind = ctx.KindSlug) must occupy one
	// end of the kind's Direction; the target is the opposite end. A slug resolves against the
	// active boards of the target-end kind in THIS instance bucket (mirrors the old ideaRef
	// resolution, now generic); NodeId-shaped values pass through untouched (ValidateLinkTargets
	// checks they resolve). Orientation: writer on the FROM end => writer is relations.from.
	static MethodologyVerdict? ResolveDirected(
		MethodologyEngineContext ctx, string writerKey, string kind, IReadOnlyList<string> refs,
		List<ResolvedLink> resolved)
	{
		var writerKind = ctx.Runtime.KindName(ctx.KindSlug);
		var dir = ctx.Runtime.LinkKind(kind)?.Direction;

		bool writerIsFrom = true;
		string? targetKind = null;
		if (dir is not null && (dir.FromKind is not null || dir.ToKind is not null))
		{
			var onFrom = dir.FromKind is not null && string.Equals(dir.FromKind, writerKind, StringComparison.OrdinalIgnoreCase);
			var onTo = dir.ToKind is not null && string.Equals(dir.ToKind, writerKind, StringComparison.OrdinalIgnoreCase);
			if (onFrom && !onTo) { writerIsFrom = true; targetKind = dir.ToKind; }
			else if (onTo && !onFrom) { writerIsFrom = false; targetKind = dir.FromKind; }
			else
				return Bad(writerKey, $"link '{kind}' cannot be set from a '{writerKind}' node — its direction (from:{dir.FromKind ?? "*"} to:{dir.ToKind ?? "*"}) includes '{writerKind}' at neither end (node '{writerKey}')");
		}
		// A null opposite end (unconstrained direction) or a direction-less kind falls back to the
		// writer kind's link constraint for its target kind; without either there is nothing to
		// resolve a slug against.
		targetKind ??= ctx.Runtime.LinkConstraints(ctx.KindSlug)
			.FirstOrDefault(c => string.Equals(c.Link, kind, StringComparison.OrdinalIgnoreCase) && c.TargetKind is not null)?.TargetKind;
		if (targetKind is null)
			return Bad(writerKey, $"link '{kind}' has no direction to resolve against — declare its direction (fromKind/toKind), or pass a NodeId (node '{writerKey}')");

		// The target-kind boards of this instance bucket (built only for a slug ref).
		var instance = ctx.MethodologyInstance;
		List<string>? boards = null;
		HashSet<string>? boardSet = null;
		foreach (var raw in refs)
		{
			var v = (raw ?? "").Trim();
			if (v.Length == 0) continue;
			if (LooksLikeNodeId(v)) { resolved.Add(new ResolvedLink(kind, writerKey, v, writerIsFrom)); continue; }
			if (boards is null)
			{
				boards = ctx.Boards
					.Where(b => !b.Closed
						&& string.Equals(b.MethodologyInstance, instance, StringComparison.OrdinalIgnoreCase)
						&& string.Equals(ctx.Runtime.KindName(b.Kind), targetKind, StringComparison.OrdinalIgnoreCase))
					.Select(b => b.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
				boardSet = boards.ToHashSet(StringComparer.Ordinal);
			}
			if (boards.Count == 0)
				return Bad(writerKey, $"links.{kind} '{raw}' (node '{writerKey}') is a slug, but no active {targetKind} board exists alongside board '{ctx.BoardName}'{InstanceSuffix(instance)} — create one or provide the target node's NodeId");
			var slug = v.ToLowerInvariant();
			var hits = ctx.NodeIndex.Values
				.Where(n => boardSet!.Contains(n.Board) && string.Equals(n.Slug, slug, StringComparison.Ordinal))
				.ToList();
			switch (hits.Count)
			{
				case 1: resolved.Add(new ResolvedLink(kind, writerKey, hits[0].NodeId, writerIsFrom)); break;
				case 0: return Bad(writerKey, $"links.{kind} '{raw}' (node '{writerKey}') does not match any node on {targetKind} board{(boards.Count > 1 ? "s" : "")} '{string.Join(", ", boards)}'");
				default: return Bad(writerKey, $"ambiguous links.{kind} '{raw}' (node '{writerKey}') — the slug matches nodes on boards: [{string.Join(", ", hits.Select(h => h.Board).OrderBy(b => b, StringComparer.Ordinal))}]; pass the target node's NodeId instead");
			}
		}
		return null;
	}

	static string InstanceSuffix(string instance) =>
		instance.Length == 0 ? "" : $" in methodology instance '{instance}'";

	// DATA-DRIVEN link constraints (primitives-link-constraints): a NEW node of a constrained type
	// must carry the constrained link in THIS call — and a PROVENANCE constraint (one that pins a
	// TARGET STATUS, like spec's idea_spec -> accepted) is re-required on EVERY write, not just at
	// creation. All messages are built FROM THE DATA (the constraint + its declared target), naming
	// the generic `links.<kind>` field — the compensation for the removed specRef/ideaRef sugar.
	static MethodologyVerdict? RequireDefinitionLinks(
		MethodologyEngineContext ctx, IReadOnlyList<NodeState> desired, IReadOnlyDictionary<string, NodeState> prior,
		List<ResolvedLink> links)
	{
		var constraints = ctx.Runtime.LinkConstraints(ctx.KindSlug);
		if (constraints.Count == 0) return null;
		var provided = links.Select(l => (l.WriterKey, Kind: l.Kind)).ToHashSet();
		var kindName = ctx.Runtime.KindName(ctx.KindSlug);
		foreach (var n in desired)
		{
			var isNew = !prior.ContainsKey(n.Key) && (n.PrevKey is null || !prior.ContainsKey(n.PrevKey));
			// The node's EFFECTIVE type: single-FSM preset kinds accept untyped nodes (an untyped
			// spec node IS a `spec`), so an empty type resolves to the kind's default before matching.
			var type = n.Type.Length == 0 ? ctx.Runtime.DefaultType(ctx.KindSlug) : n.Type;
			foreach (var c in constraints)
			{
				if (!string.Equals(c.Type, type, StringComparison.OrdinalIgnoreCase)) continue;
				// PROVENANCE = the constraint pins a target STATUS: the authorizing node must exist in
				// that status on every write (the old idea_spec `every write` cadence, now data).
				var everyWrite = c.TargetStatuses is { Count: > 0 };
				if (!isNew && !everyWrite) continue;
				if (provided.Contains((n.Key, c.Link.ToLowerInvariant())) || provided.Contains((n.Key, c.Link)))
					continue;
				// "work feature" / "spec" (a single-type kind reads its kind name once, not "spec spec").
				var subject = string.Equals(kindName, type, StringComparison.OrdinalIgnoreCase) ? kindName : $"{kindName} {type}";
				var cadence = everyWrite ? $"every write of a {subject}" : $"a new {subject}";
				var target = TargetClause(c);
				return Bad(n.Key, $"{cadence} must carry a {c.Link} link — provide links.{c.Link}{target} (node '{n.Key}')");
			}
		}
		return null;
	}

	// The constraint's declared target as a compact " — points at a `<kind>` node [in status a|b]"
	// clause (empty when the constraint pins neither) for the require/target messages.
	static string TargetClause(MethodologyLinkConstraintDef c) =>
		(c.TargetKind, c.TargetStatuses) switch
		{
			(null, null or { Count: 0 }) => "",
			({ } k, null or { Count: 0 }) => $" — points at a `{k}` node",
			(null, { } s) => $" — points at a node in status {string.Join("|", s)}",
			({ } k, { } s) => $" — points at a `{k}` node in status {string.Join("|", s)}",
		};

	// DATA-DRIVEN link-target guard (was ValidateSpecRefs + RequireAcceptedIdeaForSpec): every
	// resolved DIRECTED link must resolve to a real node, and when the writing kind's constraints
	// declare a target for that link kind (TargetKind / TargetStatuses), the target's EFFECTIVE
	// kind and status must match. `blocks` edges are same-board rows already resolved, not target-
	// checked here. The SpecBoard auto-wire pin stays alongside, now DATA-driven: it binds any link
	// whose target kind is the writer kind's AutoWireSpecFrom (work->spec) to the pinned board.
	static MethodologyVerdict? ValidateLinkTargets(
		MethodologyEngineContext ctx, List<ResolvedLink> links)
	{
		if (links.Count == 0) return null;
		var constraints = ctx.Runtime.LinkConstraints(ctx.KindSlug);
		var kindName = ctx.Runtime.KindName(ctx.KindSlug);
		var autoWire = ctx.Runtime.AutoWireSpecFrom(ctx.KindSlug);
		foreach (var link in links)
		{
			if (string.Equals(link.Kind, "blocks", StringComparison.OrdinalIgnoreCase)) continue;
			if (!ctx.NodeIndex.TryGetValue(link.TargetNodeId, out var t))
				return Bad(link.WriterKey, $"links.{link.Kind} '{link.TargetNodeId}' (node '{link.WriterKey}') does not resolve to any node");
			var targetKindName = ctx.Runtime.KindName(t.BoardKind);
			var rule = constraints.FirstOrDefault(c =>
				string.Equals(c.Link, link.Kind, StringComparison.OrdinalIgnoreCase)
				&& (c.TargetKind is not null || c.TargetStatuses is { Count: > 0 }));
			if (rule is not null)
			{
				if (rule.TargetKind is not null && !string.Equals(targetKindName, rule.TargetKind, StringComparison.OrdinalIgnoreCase))
					return Bad(link.WriterKey, $"links.{link.Kind} '{link.TargetNodeId}' (node '{link.WriterKey}') points to board '{t.Board}', which is not a {rule.TargetKind} board");
				if (rule.TargetStatuses is { Count: > 0 } ts && !ts.Contains(t.Status, StringComparer.OrdinalIgnoreCase))
					return Bad(link.WriterKey, $"links.{link.Kind} '{link.TargetNodeId}' (node '{link.WriterKey}') target is '{t.Status}', not {string.Join("|", ts)} — a {kindName} change needs a {rule.TargetKind ?? link.Kind} node in status {string.Join("|", ts)}");
			}
			// Auto-wire board pin: a work board that links a specific spec board requires its
			// task_spec targets to live on it (the kind's AutoWireSpecFrom names that target kind).
			if (autoWire is not null && ctx.SpecBoard is { Length: > 0 } sb
				&& string.Equals(targetKindName, autoWire, StringComparison.OrdinalIgnoreCase)
				&& !string.Equals(t.Board, sb, StringComparison.Ordinal))
				return Bad(link.WriterKey, $"links.{link.Kind} '{link.TargetNodeId}' (node '{link.WriterKey}') is on board '{t.Board}', but this board links spec board '{sb}'");
		}
		return null;
	}

	// Invariant: a node in the kind's blocking-gate status (MethodologyKindDef.BlocksGate,
	// spec methodology-blocks-gate-data) must name a blocker (blockedBy in this call, or an
	// already-active `blocks` edge into it) — a STATE invariant, checked on every write of a
	// gated node (including birth), not a gate on the transition that lands it there.
	// BlocksGate(...), NOT IsWorkKind/PresetKind(...) == BoardKind.Work: the gate status is now
	// DATA on the kind (was the literal "Blocked", gated on IsWorkKind — itself a fix for
	// presetkind-spec-blind-spot: PresetKind nulls out for any DEFINED kind, and `work` is one of
	// the quartet's four kinds). A definition-declared kind can opt into this same invariant
	// under its own gate status by declaring BlocksGate; today only the `work` preset does.
	public static MethodologyVerdict? RequireBlockers(
		MethodologyEngineContext ctx, IReadOnlyList<NodeState> desired, List<ResolvedLink> links)
	{
		if (ctx.Runtime.BlocksGate(ctx.KindSlug) is not { } gate) return null;
		var blockedKeys = links.Where(l => string.Equals(l.Kind, "blocks", StringComparison.OrdinalIgnoreCase))
			.Select(l => l.WriterKey).ToHashSet(StringComparer.Ordinal);
		foreach (var n in desired)
		{
			if (!string.Equals(n.Status, gate.Status, StringComparison.OrdinalIgnoreCase)) continue;
			if (blockedKeys.Contains(n.Key)) continue;
			// A node born in this call carries a fresh NodeId, so it is absent from the prefetched
			// edge map — exactly as the old per-node live read came back empty for it.
			var hasActiveBlocker = ctx.BlockerEdgesByNodeId.TryGetValue(n.NodeId, out var edges) && edges.Count > 0;
			if (!hasActiveBlocker)
				return Bad(n.Key, $"a {gate.Status} task must name a blocker — provide blockedBy (node '{n.Key}')");
		}
		return null;
	}

	// DATA-DRIVEN transition gate (idea-review-needs-plan generalized; schema v2, spec
	// methodology-gate-strictness): a transition whose definition names a PreconditionArtifact
	// requires an active `artifact:<slug>` comment on the node before it fires — the ideas
	// preset gates exploring->review on `artifact:spec_plan`, a definition kind declares its
	// own. Mirrors WorkflowEngine's from-resolution (unchanged status skipped, unknown prior =
	// recovery); landing DIRECTLY in a gated target status at creation is refused too, so the
	// gate can't be bypassed by birth. NodeId comes from the prior row (desired rows get their
	// NodeId assigned inside the temporal upsert, after this check). `EnforceArtifacts` false
	// demotes the gate to a convention the guide states but the server does not block — the
	// default is true, which reproduces today's unconditional hard gate for every existing
	// definition. (A definition may declare MORE than one non-inline requiredArtifacts entry per
	// transition — only the first is enforced here; see the note on WorkflowTransition.EnforceArtifacts.)
	public static MethodologyVerdict? RequirePreconditionArtifacts(
		MethodologyEngineContext ctx, IReadOnlyList<NodeState> desired, IReadOnlyDictionary<string, NodeState> prior)
	{
		foreach (var d in desired)
		{
			var wf = ctx.Runtime.For(ctx.KindSlug, d.Type.Length == 0 ? null : d.Type);
			if (wf is null) continue; // ApplyWorkflow already rejected the unknown type
			var p = prior.GetValueOrDefault(d.Key) ?? (d.PrevKey is not null ? prior.GetValueOrDefault(d.PrevKey) : null);
			var from = p?.Status;
			if (from is not null && string.Equals(from, d.Status, StringComparison.OrdinalIgnoreCase)) continue; // unchanged
			if (from is not null && wf.Status(from) is null) from = null; // recovery — mirrors WorkflowEngine

			string? artifact;
			bool enforceArtifacts;
			string transition;
			if (from is null)
			{
				// No transition fired (creation/recovery) — but if any transition INTO this status
				// is gated, entering it directly must satisfy the same artifact.
				var gated = wf.Transitions.FirstOrDefault(t =>
					t.PreconditionArtifact is not null && string.Equals(t.To, d.Status, StringComparison.OrdinalIgnoreCase));
				if (gated is null) continue;
				artifact = gated.PreconditionArtifact;
				enforceArtifacts = gated.EnforceArtifacts;
				transition = $"'{gated.From}' -> '{gated.To}'";
			}
			else
			{
				var tr = wf.Transition(from, d.Status);
				if (tr?.PreconditionArtifact is null) continue;
				artifact = tr.PreconditionArtifact;
				enforceArtifacts = tr.EnforceArtifacts;
				transition = $"'{from}' -> '{d.Status}'";
			}
			if (!enforceArtifacts) continue; // convention only — declared, not server-blocked

			var tag = $"artifact:{artifact}";
			if (p is null || p.NodeId.Length == 0)
				return new(d.Key, $"node '{d.Key}' can't be created directly in '{d.Status}' — transition {transition} requires an {tag} comment; create the node, add the comment, then transition", VerdictKind.InvalidOperation);
			var tags = ctx.CommentTagsByNodeId.GetValueOrDefault(p.NodeId, []);
			if (!tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)))
				return new(d.Key, $"transition {transition} on node '{d.Key}' requires an {tag} comment (the transition's precondition artifact) — add the comment, then retry", VerdictKind.InvalidOperation);
		}
		return null;
	}
}
