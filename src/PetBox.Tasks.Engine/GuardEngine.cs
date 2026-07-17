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

	// ONE decision per pass of the upsert's retry loop: resolve the refs, then judge, in the
	// order the service used to call them (ResolveSpecRefs -> ResolveBlockedBy -> ResolveIdeaRefs
	// -> RequireDefinitionLinks -> ValidateLinkTargets -> RequireBlockers ->
	// RequirePreconditionArtifacts). That order IS the order of indictment when a batch violates
	// several rules at once, and it is preserved here by construction.
	public static MethodologyEngineDecision Decide(
		MethodologyEngineContext ctx,
		IReadOnlyList<NodeState> desired,
		IReadOnlyDictionary<string, NodeState> prior,
		IReadOnlyDictionary<string, string> specRefsRaw,
		IReadOnlyDictionary<string, string> blockedByRaw,
		IReadOnlyDictionary<string, string> ideaRefsRaw)
	{
		if (ResolveSpecRefs(ctx, specRefsRaw, out var specRefs) is { } v1)
			return MethodologyEngineDecision.Refused(v1);
		if (ResolveBlockedBy(ctx, desired, prior, blockedByRaw, out var blockedBy) is { } v2)
			return MethodologyEngineDecision.Refused(v2);
		if (ResolveIdeaRefs(ctx, ideaRefsRaw, out var ideaRefs) is { } v3)
			return MethodologyEngineDecision.Refused(v3);
		if (RequireDefinitionLinks(ctx, desired, prior, specRefs, blockedBy, ideaRefs) is { } v4)
			return MethodologyEngineDecision.Refused(v4);
		if (ValidateLinkTargets(ctx, specRefs, ideaRefs) is { } v5)
			return MethodologyEngineDecision.Refused(v5);
		if (RequireBlockers(ctx, desired, blockedBy) is { } v6)
			return MethodologyEngineDecision.Refused(v6);
		if (RequirePreconditionArtifacts(ctx, desired, prior) is { } v7)
			return MethodologyEngineDecision.Refused(v7);
		return new MethodologyEngineDecision([], specRefs, blockedBy, ideaRefs);
	}

	static MethodologyVerdict Bad(string node, string message) => new(node, message, VerdictKind.InvalidArgument);

	// specRef accepts the spec node's slug OR its NodeId, mirroring part_of (ResolveParentId).
	// A slug resolves against the board's linked spec board (SpecBoard); the returned map holds
	// NodeIds only. NodeId-shaped values pass through untouched (existing behavior).
	public static MethodologyVerdict? ResolveSpecRefs(
		MethodologyEngineContext ctx, IReadOnlyDictionary<string, string> raw,
		out IReadOnlyDictionary<string, string> resolved)
	{
		var map = new Dictionary<string, string>(raw, StringComparer.Ordinal);
		resolved = map;
		if (map.Count == 0) return null;
		foreach (var (key, val) in raw)
		{
			var v = val.Trim();
			if (LooksLikeNodeId(v)) { map[key] = v; continue; }
			if (ctx.SpecBoard is not { Length: > 0 } sb)
				return Bad(key, $"specRef '{val}' (node '{key}') is a slug, but this board has no linked spec board — provide the spec node's NodeId");
			var slug = v.ToLowerInvariant();
			var target = ctx.NodeIndex.Values.FirstOrDefault(n =>
				string.Equals(n.Board, sb, StringComparison.Ordinal) && string.Equals(n.Slug, slug, StringComparison.Ordinal));
			if (target is null)
				return Bad(key, $"specRef '{val}' (node '{key}') does not match any node on spec board '{sb}'");
			map[key] = target.NodeId;
		}
		return null;
	}

	// blockedBy accepts the blocker's slug OR its NodeId, mirroring specRef. A slug resolves on
	// THIS board (blockers are usually siblings) over the active rows overlaid with this batch, so
	// a blocker created in the same call resolves too. The returned map holds NodeIds only.
	public static MethodologyVerdict? ResolveBlockedBy(
		MethodologyEngineContext ctx, IReadOnlyList<NodeState> desired, IReadOnlyDictionary<string, NodeState> prior,
		IReadOnlyDictionary<string, string> raw, out IReadOnlyDictionary<string, string> resolved)
	{
		var map = new Dictionary<string, string>(raw, StringComparer.Ordinal);
		resolved = map;
		if (map.Count == 0) return null;
		var slugToId = prior.Values.Where(n => n.NodeId.Length > 0)
			.ToDictionary(n => n.Key, n => n.NodeId, StringComparer.Ordinal);
		foreach (var n in desired.Where(n => n.NodeId.Length > 0))
			slugToId[n.Key] = n.NodeId;
		foreach (var (key, val) in raw)
		{
			var v = val.Trim();
			if (LooksLikeNodeId(v)) { map[key] = v; continue; }
			if (!slugToId.TryGetValue(v.ToLowerInvariant(), out var id))
				return Bad(key, $"blockedBy '{val}' (node '{key}') does not match any node on board '{ctx.Board}' — a blocker's slug resolves on the same board; pass a NodeId to reference a node on another board");
			map[key] = id;
		}
		return null;
	}

	// ideaRef accepts the idea node's slug OR its NodeId, mirroring specRef. There is no
	// SpecBoard-style pin for ideas, so a slug resolves against the board(s) whose kind is the
	// idea_spec target kind (`ideas`) INSIDE THIS BOARD'S methodology-instance bucket — the same
	// membership grouping AutoWireSpec wires work->spec within, so a multi-instance project never
	// resolves an ideaRef across instances. Any active row matches: no status filter (the only
	// legal target is TERMINAL `accepted`; the kind/status constraint still runs afterwards in
	// ValidateLinkTargets). NodeId-shaped values pass through untouched.
	public static MethodologyVerdict? ResolveIdeaRefs(
		MethodologyEngineContext ctx, IReadOnlyDictionary<string, string> raw,
		out IReadOnlyDictionary<string, string> resolved)
	{
		var map = new Dictionary<string, string>(raw.Count, StringComparer.Ordinal);
		resolved = map;
		if (raw.Count == 0) return null;
		// Every value is trimmed, NodeId-shaped or not (historical behavior).
		var slugRefs = new List<KeyValuePair<string, string>>();
		foreach (var (key, val) in raw)
		{
			map[key] = val.Trim();
			if (!LooksLikeNodeId(val.Trim())) slugRefs.Add(new(key, val));
		}
		if (slugRefs.Count == 0) return null;

		// The target kind is DATA: the writing kind's idea_spec constraint names it (spec preset:
		// `ideas`); a definition that declares no target falls back to the preset ideas kind.
		// (The old code read `board.Kind` here and `kindSlug` in the other guards — UpsertAsync
		// binds `kindSlug = meta.Kind`, so they are the same value; one field expresses it.)
		var targetKind = ctx.Runtime.LinkConstraints(ctx.KindSlug)
			.FirstOrDefault(c => string.Equals(c.Link, "idea_spec", StringComparison.OrdinalIgnoreCase) && c.TargetKind is not null)
			?.TargetKind ?? BoardKind.Ideas.ToString().ToLowerInvariant();

		var instance = ctx.MethodologyInstance;
		var ideaBoards = ctx.Boards
			.Where(b => !b.Closed
				&& string.Equals(b.MethodologyInstance, instance, StringComparison.OrdinalIgnoreCase)
				&& string.Equals(ctx.Runtime.KindName(b.Kind), targetKind, StringComparison.OrdinalIgnoreCase))
			.Select(b => b.Name)
			.OrderBy(n => n, StringComparer.Ordinal)
			.ToList();

		var (firstKey, firstRaw) = slugRefs[0];
		if (ideaBoards.Count == 0)
			return Bad(firstKey, $"ideaRef '{firstRaw}' (node '{firstKey}') is a slug, but no active {targetKind} board exists alongside board '{ctx.BoardName}'{InstanceSuffix(instance)} — create one or provide the idea node's NodeId");

		var boardList = string.Join(", ", ideaBoards);
		var boardSet = ideaBoards.ToHashSet(StringComparer.Ordinal);
		foreach (var (key, val) in slugRefs)
		{
			var slug = val.Trim().ToLowerInvariant();
			var hits = ctx.NodeIndex.Values
				.Where(n => boardSet.Contains(n.Board) && string.Equals(n.Slug, slug, StringComparison.Ordinal))
				.ToList();
			switch (hits.Count)
			{
				case 1: map[key] = hits[0].NodeId; break;
				case 0: return Bad(key, $"ideaRef '{val}' (node '{key}') does not match any node on {targetKind} board{(ideaBoards.Count > 1 ? "s" : "")} '{boardList}'");
				default: return Bad(key, $"ambiguous ideaRef '{val}' (node '{key}') — the slug matches nodes on boards: [{string.Join(", ", hits.Select(h => h.Board).OrderBy(b => b, StringComparer.Ordinal))}]; pass the idea node's NodeId instead");
			}
		}
		return null;
	}

	static string InstanceSuffix(string instance) =>
		instance.Length == 0 ? "" : $" in methodology instance '{instance}'";

	// DATA-DRIVEN link constraints (primitives-link-constraints): a NEW node of a constrained type
	// must carry the constrained link in THIS call (task_spec = specRef, blocks = blockedBy,
	// idea_spec = ideaRef — the validator admits only these). CADENCE follows the builtin link
	// kind's semantics: task_spec/blocks are STRUCTURAL edges, required at creation only (edits
	// don't re-require them); idea_spec is a PROVENANCE edge — every write of a constrained type
	// must name the idea that authorizes it (the spec-write governance, now as data).
	public static MethodologyVerdict? RequireDefinitionLinks(
		MethodologyEngineContext ctx, IReadOnlyList<NodeState> desired, IReadOnlyDictionary<string, NodeState> prior,
		IReadOnlyDictionary<string, string> specRefs, IReadOnlyDictionary<string, string> blockedBy,
		IReadOnlyDictionary<string, string> ideaRefs)
	{
		var constraints = ctx.Runtime.LinkConstraints(ctx.KindSlug);
		if (constraints.Count == 0) return null;
		foreach (var n in desired)
		{
			var isNew = !prior.ContainsKey(n.Key) && (n.PrevKey is null || !prior.ContainsKey(n.PrevKey));
			// The node's EFFECTIVE type: single-FSM preset kinds accept untyped nodes (an untyped
			// spec node IS a `spec`), so an empty type resolves to the kind's default before
			// constraint matching — the old kind-wide guard's reach, as data.
			// The MESSAGES below interpolate THIS, never n.Type: the verdict must name the type it
			// matched on, or an untyped node reads "a work  must link ..." — indicted by a rule it
			// refuses to name. Matching and wording share one variable so they can't drift apart.
			var type = n.Type.Length == 0 ? ctx.Runtime.DefaultType(ctx.KindSlug) : n.Type;
			foreach (var c in constraints)
			{
				if (!string.Equals(c.Type, type, StringComparison.OrdinalIgnoreCase)) continue;
				var everyWrite = string.Equals(c.Link, "idea_spec", StringComparison.OrdinalIgnoreCase);
				if (!isNew && !everyWrite) continue;
				var kindName = ctx.Runtime.KindName(ctx.KindSlug);
				// task_spec keeps the historical, target-naming wording; idea_spec states the
				// provenance rule with the declared target; the generic form names the link kind
				// + the upsert field that expresses it.
				var message = c.Link.ToLowerInvariant() switch
				{
					"task_spec" when !specRefs.ContainsKey(n.Key) =>
						$"a {kindName} {type} must link a {c.TargetKind ?? "spec"} node — provide specRef (node '{n.Key}')",
					"blocks" when !blockedBy.ContainsKey(n.Key) =>
						$"a {kindName} {type} must carry a {c.Link} link at creation — provide blockedBy (node '{n.Key}')",
					// idea_spec — the validator admits no other kind
					"idea_spec" when !ideaRefs.ContainsKey(n.Key) =>
						c.TargetStatuses is { Count: > 0 } ts
							? $"a {kindName} change must be made under {string.Join("|", ts)} {c.TargetKind ?? c.Link} — provide ideaRef (node '{n.Key}')"
							: $"a {kindName} {type} must carry a {c.Link} link on every write — provide ideaRef (node '{n.Key}')",
					_ => null,
				};
				if (message is not null) return Bad(n.Key, message);
			}
		}
		return null;
	}

	// DATA-DRIVEN link-target guard (schema v2, was ValidateSpecRefs + RequireAcceptedIdeaForSpec):
	// every PROVIDED ref must resolve to a real node, and when the writing kind's constraints
	// declare a target for that link kind (TargetKind / TargetStatuses), the target's EFFECTIVE
	// kind and status must match. The target rule is KIND-level, not type-level: the constraint's
	// Type says who MUST carry the link, its target says what the link points at for this kind —
	// so a voluntary ref (e.g. a chore's specRef) is validated by the same rule. The SpecBoard
	// binding stays alongside: it is the quartet auto-wire's board pin, data on the board meta
	// rather than the methodology.
	public static MethodologyVerdict? ValidateLinkTargets(
		MethodologyEngineContext ctx,
		IReadOnlyDictionary<string, string> specRefs, IReadOnlyDictionary<string, string> ideaRefs)
	{
		if (specRefs.Count == 0 && ideaRefs.Count == 0) return null;
		var constraints = ctx.Runtime.LinkConstraints(ctx.KindSlug);

		if (ValidateRefs("specRef", "task_spec", specRefs) is { } v) return v;
		if (ValidateRefs("ideaRef", "idea_spec", ideaRefs) is { } v2) return v2;

		// Auto-wire pin: when this board links a specific spec board, every specRef must live on
		// it (same message as always).
		if (ctx.SpecBoard is { Length: > 0 } sb)
			foreach (var (key, refId) in specRefs)
				if (ctx.NodeIndex.TryGetValue(refId, out var t) && !string.Equals(t.Board, sb, StringComparison.Ordinal))
					return Bad(key, $"specRef '{refId}' (node '{key}') is on board '{t.Board}', but this work board links spec board '{sb}'");
		return null;

		MethodologyVerdict? ValidateRefs(string field, string link, IReadOnlyDictionary<string, string> refs)
		{
			if (refs.Count == 0) return null;
			// The kind's target rule for this link kind: the first constraint declaring one (the
			// validator keeps (type, link) pairs unique; quartet declares one rule per link).
			var rule = constraints.FirstOrDefault(c =>
				string.Equals(c.Link, link, StringComparison.OrdinalIgnoreCase)
				&& (c.TargetKind is not null || c.TargetStatuses is { Count: > 0 }));
			foreach (var (key, refId) in refs)
			{
				if (!ctx.NodeIndex.TryGetValue(refId, out var t))
					return Bad(key, $"{field} '{refId}' (node '{key}') does not resolve to any node");
				if (rule is null) continue;
				if (rule.TargetKind is not null && !string.Equals(ctx.Runtime.KindName(t.BoardKind), rule.TargetKind, StringComparison.OrdinalIgnoreCase))
					return Bad(key, $"{field} '{refId}' (node '{key}') points to board '{t.Board}', which is not a {rule.TargetKind} board");
				if (rule.TargetStatuses is { Count: > 0 } ts && !ts.Contains(t.Status, StringComparer.OrdinalIgnoreCase))
					return Bad(key, $"{field} '{refId}' (node '{key}') target is '{t.Status}', not {string.Join("|", ts)} — a {ctx.Runtime.KindName(ctx.KindSlug)} change needs a {rule.TargetKind ?? link} node in status {string.Join("|", ts)}");
			}
			return null;
		}
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
		MethodologyEngineContext ctx, IReadOnlyList<NodeState> desired, IReadOnlyDictionary<string, string> blockedBy)
	{
		if (ctx.Runtime.BlocksGate(ctx.KindSlug) is not { } gate) return null;
		foreach (var n in desired)
		{
			if (!string.Equals(n.Status, gate.Status, StringComparison.OrdinalIgnoreCase)) continue;
			if (blockedBy.ContainsKey(n.Key)) continue;
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
