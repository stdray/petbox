using System.Text.Json;
using System.Text.Json.Serialization;
using PetBox.Core.Contract;

namespace PetBox.Web.Mcp.Contract;

// One value of the `links` map: a single ref OR an array of refs. Accepts the wire shape
// `{ "task_spec": "slug" }` and `{ "task_spec": ["a", "b"] }` alike, normalizing to a string list
// on parse (spec methodology-link-kinds-declared). Serializes as an array.
[JsonConverter(typeof(LinkRefsConverter))]
public sealed record LinkRefs(IReadOnlyList<string> Values);

// string|array -> LinkRefs. A JSON string becomes a one-element list; a JSON array reads its
// string elements (nulls skipped); null/anything else becomes an empty list (the service reports
// the actionable error, not the deserializer).
public sealed class LinkRefsConverter : JsonConverter<LinkRefs>
{
	public override LinkRefs Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
	{
		switch (reader.TokenType)
		{
			case JsonTokenType.String:
				return new LinkRefs([reader.GetString() ?? ""]);
			case JsonTokenType.StartArray:
				var list = new List<string>();
				while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
					if (reader.TokenType == JsonTokenType.String && reader.GetString() is { } s)
						list.Add(s);
				return new LinkRefs(list);
			default:
				reader.Skip();
				return new LinkRefs([]);
		}
	}

	public override void Write(Utf8JsonWriter writer, LinkRefs value, JsonSerializerOptions options)
	{
		writer.WriteStartArray();
		foreach (var v in value.Values) writer.WriteStringValue(v);
		writer.WriteEndArray();
	}
}

// Typed MCP tool-INPUT records (typed-surface Phase 4 — typed inputs). The structural
// array params of tasks_upsert (`nodes`) and memory_upsert (`entries`) used to arrive as a
// raw JsonElement, so the SDK-generated inputSchema was an opaque blob and the per-field
// types were invisible to the client (the class of bug that produced the config_binding
// stringified-object trap). Making the param a typed array (TaskNodeInput[] / MemoryEntryInputDto[])
// makes the SDK emit a real per-field array schema.
//
// STALE-SCHEMA TRADE-OFF: the old JsonElement parser also accepted the array double-encoded as
// a JSON *string* ("[{...}]") — the MCP stale-schema gotcha (a client whose cached schema still
// believes the param is a string). With a typed-array parameter the SDK deserializes the
// argument into TaskNodeInput[] BEFORE our code runs, so a string payload now fails to bind at
// the SDK layer (a custom tolerant converter would flatten the element schema back to opaque,
// defeating the whole point). This is an accepted deviation: the typed schema is the primary
// goal, and the stale-string risk is bounded to within a single MCP session (a reconnect
// refreshes the cached schema and fixes it). See the report.

// A node as submitted to tasks_upsert. Mirrors EXACTLY the fields the old JsonElement parser
// (TasksTools.ParseNodePatches/ParseTags/ResolveKey/ResolvePrevKey) accepted. Field semantics
// (null = omit/inherit, "" = explicit clear) are unchanged — they are now carried by the JSON
// value itself (an omitted property deserializes to null = inherit; an explicit "" stays "").
public sealed record TaskNodeInput
{
	// Flat board-unique slug — the KEY FIELD this write sets, never a reference. Typed nullable
	// only so a missing value reaches ResolveKey's own message instead of an SDK bind error; the
	// generated schema lists it in `required` (see McpOutputSchema.RequiredNodeKey). The legacy
	// `l1` alias was REMOVED (drop-legacy-aliases) — `l1` is now an unknown member and is
	// REJECTED by McpUnknownParameterFilter, not silently dropped.
	[McpRequiredMember]
	public string? Key { get; init; }

	// Rename source slug (PrevKey). Not an alias: it names the node's PREVIOUS key so a rename
	// can be expressed in one PATCH. The legacy `prevL1` alias was REMOVED.
	public string? PrevKey { get; init; }

	// Vertical decomposition: the parent node REFERENCE — its slug key or its 32-hex NodeId
	// (both accepted). null = omit, "" = detach to root.
	public string? PartOf { get; init; }

	// Per-node links, addressed by relation-kind slug (spec methodology-link-kinds-declared):
	// { "<kind>": ref } or { "<kind>": [ref, …] }, each ref a slug or NodeId. Replaces the removed
	// specRef/ideaRef sugar — a spec write passes `links:{ idea_spec: <idea> }`, a work task
	// `links:{ task_spec: <spec> }`. The target end and edge orientation come from the kind's
	// declared Direction. `blockedBy` stays as sugar (builtin `blocks`); `links.blocks` is also
	// accepted, but not both on one node.
	public Dictionary<string, LinkRefs>? Links { get; init; }
	// blockedBy → the blocking node (builtin `blocks`); supersedes → the node this one replaces.
	// Each is a node REFERENCE — its slug key or its 32-hex NodeId (both accepted). Kept as sugar
	// for the direction-less builtin structural edges. Neither is an alias: `supersedes` names a
	// DIFFERENT node, it does not restate another parameter.
	public string? BlockedBy { get; init; }
	public string? Supersedes { get; init; }

	public string? Status { get; init; }
	public string? Type { get; init; }
	public string? Title { get; init; }
	public string? Body { get; init; }
	// Point edit of the body: a list of {old, new} substitutions applied IN ORDER against the
	// node's CURRENT body, so the call costs the size of the CHANGE rather than the size of the
	// node (spec/write-cost-follows-change). Mutually exclusive with `body`. An `old` that matches
	// zero times, or more than once, REFUSES the whole write through conflicts[] — never a
	// first-match guess, never a partial application.
	public IReadOnlyList<FragmentEditDto>? Fragment { get; init; }
	// A body that ALREADY EXISTS as a file, carried by REFERENCE instead of retyped into this call
	// (work/write-body-by-reference). Upload the file to POST /api/blobs/{projectKey} — a RAW body,
	// not JSON, which is the point: the text never passes through the model's output and so never
	// pays the \uXXXX escaping tax that truncates long Cyrillic calls. Send the `ref` it returns
	// here, verbatim. ONE-SHOT: consumed by the write that references it (a refused write does NOT
	// consume it — retry with the same ref), and expiring 24h after upload otherwise.
	// Mutually exclusive with `body` AND with `fragment`: two of them in one item is a REFUSAL
	// through conflicts[], never a silent precedence — the same rule `body` vs `fragment` follows.
	public string? BodyRef { get; init; }
	// Free-form reason for THIS write (not the node body). Two independent effects: (1) a status
	// transition the methodology marks RequiresReason still fails outright when this is
	// omitted/null/whitespace — that gate is unchanged. (2) whenever this write is APPLIED and
	// the value is non-empty, it is persisted as an `artifact:reason` comment on the node —
	// birth, any transition (gated or not), or a plain field edit alike, not only the gated
	// transition that demanded one. A resubmit whose text matches the node's most recent
	// artifact:reason comment is deduped (no second comment).
	public string? Reason { get; init; }

	// Attached commit SHAs (node-commits-impl). null = omit (don't touch); a non-null list
	// (incl. empty) REPLACES the node's full commit set — same semantics as Tags.
	public IReadOnlyList<string>? Commits { get; init; }

	// owner-decision-pending-flag: "this node is waiting on a decision from the owner". null =
	// omit (leave as-is), true/false = an explicit set/clear; a new node starts false. Orthogonal
	// to `status` — a node can be InProgress AND waiting — and settable by an agent or the owner.
	public bool? DecisionPending { get; init; }

	// Baseline version last seen (0 = new); sparse ordering int.
	public long Version { get; init; }
	public long? Priority { get; init; }

	// Enforced tags ("namespace:value", namespaces area|concern). null = omit; a non-null list
	// (incl. empty) REPLACES the node's full tag set. A CSV string is also tolerated on the wire.
	public IReadOnlyList<string>? Tags { get; init; }

	// Soft-delete marker: { key, deleted:true } temporal-closes the active node (optional
	// version baseline). Mirrors memory_upsert's marker; other fields are ignored.
	public bool Deleted { get; init; }
}

// The `definition` argument of tasks_methodology_template_upsert / rules_upsert — the whole
// methodology document as a structured record (typed, not a JSON string/blob, per the
// typed-surface convention). Mirrors MethodologyDefinition; the adapter maps it 1:1 and
// the service validates integrity (slugs, per-block references, uniqueness).
public sealed record MethodologyDefInput
{
	// Methodology name, a slug ([a-z][a-z0-9_-]{0,99}).
	public string? Name { get; init; }
	public MethodologyKindInput[]? Kinds { get; init; }
	// Project-declared relation kinds (effect-free), usable in relations_create alongside
	// the builtin process + neutral kinds.
	public MethodologyLinkKindInput[]? LinkKinds { get; init; }
	// Declared tag namespaces: when present, tags on definition-resolved boards must be
	// `<namespace>:value` with the namespace from this list; empty/omitted = free-form.
	public MethodologyTagAxisInput[]? TagAxes { get; init; }
	// Mirrors MethodologyDefinition.StrictMode (spec methodology-gate-strictness): the default
	// a transition's approval gate falls back to when it declares no `enforce.approval` of its
	// own. Default false reproduces today's behavior (owner-only by convention) for every kind
	// this definition declares; a preset kind (not declared here) is never strict.
	public bool StrictMode { get; init; }
}

// One board kind of the methodology. `kind` is a FREE-FORM slug (user-defined kinds are
// the point — not limited to the built-in simple|spec|ideas|intake|work).
//
// MUST mirror MethodologyKindDef (PetBox.Tasks.Workflow) FIELD FOR FIELD. rules_upsert /
// template_upsert perform a FULL-DOCUMENT REPLACE keyed off this type — any domain field
// missing here is silently discarded on every edit, no error, no warning (root cause of
// work/mcp-rules-upsert-is-lossy: AutoWireFrom/Delivery/DefaultView/OutlineReveal were
// wiped in prod TWICE this way before this parity was enforced by
// MethodologyKindContractParityTests). Add a domain field → add it here too, or the arch
// test in that file goes red.
public sealed record MethodologyKindInput
{
	public string? Kind { get; init; }
	// Whether the bare board quick-add form may create nodes of this kind (default true;
	// the built-in preset turns it off where a node needs a link at birth: spec/work).
	public bool QuickAddAllowed { get; init; } = true;
	public MethodologyWorkflowInput[]? Workflows { get; init; }
	// Per-type creation link requirements ("a new <type> must carry a <link>"); omitted =
	// no requirement (constraints are opt-in per type).
	public MethodologyLinkConstraintInput[]? LinkConstraints { get; init; }
	// Declared transition effects ("on entering <on>, set <direction> <link> nodes to
	// <set>"); omitted = none. Declaration only — the engine executes them in a later wave.
	public MethodologyEffectInput[]? Effects { get; init; }
	// Mirrors MethodologyKindDef.AutoWireFrom: auto-wire this kind's WiredBoard to the
	// sole active board of this kind when the condition holds. Null = no auto-wire.
	public string? AutoWireFrom { get; init; }
	// Mirrors MethodologyKindDef.Delivery: the bottom-up delivery roll-up for this kind.
	// Null = no delivery computation.
	public MethodologyDeliveryInput? Delivery { get; init; }
	// Mirrors MethodologyKindDef.DefaultView (BoardViewModeNames). Null = builtin default.
	public string? DefaultView { get; init; }
	// Mirrors MethodologyKindDef.OutlineReveal (OutlineRevealModeNames). Null = builtin
	// default.
	public string? OutlineReveal { get; init; }
	// Mirrors MethodologyKindDef.Singleton: true = at most one open board of this kind per
	// methodology instance. Null = no opinion (falls back to the preset's, else false).
	public bool? Singleton { get; init; }
	// Mirrors MethodologyKindDef.BlocksGate: the blocking-gate statuses (a node in `status`
	// must name a blocker; a released node moves to `releaseTo`). Null = this kind has no
	// blocking gate.
	public MethodologyBlocksGateInput? BlocksGate { get; init; }
	// Mirrors MethodologyKindDef.Description: free-form prose about this kind (spec
	// methodology-primitive-descriptions). Null = none.
	public string? Description { get; init; }
	// Mirrors MethodologyKindDef.BoardName: the preferred board name for this kind, tried
	// first by PickBoardName. Null = no opinion (falls back to the kind-slug-derived names).
	public string? BoardName { get; init; }
}

// Mirrors MethodologyBlocksGateDef 1:1 (see the parity note on MethodologyKindInput above).
public sealed record MethodologyBlocksGateInput
{
	public string? Status { get; init; }
	public string? ReleaseTo { get; init; }
}

// Mirrors MethodologyDeliveryDef 1:1 (see the parity note on MethodologyKindInput above).
public sealed record MethodologyDeliveryInput
{
	public string[]? RequiredTypes { get; init; }
	public string[]? DefectTypes { get; init; }
	// Mirrors MethodologyDeliveryDef.Link: the relation kind the roll-up sweeps inbound edges of
	// (the quartet spec rolls up over task_spec). Required — no default.
	public string? Link { get; init; }
}

// "A NEW node of type `type` must carry a link of kind `link` at creation." `link` must
// be upsert-expressible: any declared/builtin link, addressed via links:{kind:ref};
// blockedBy is builtin-blocks sugar.
// `targetKind`/`targetStatuses` optionally declare what the link must point at: a node of
// that kind and/or in one of those statuses (declaration only in this wave).
public sealed record MethodologyLinkConstraintInput
{
	public string? Type { get; init; }
	public string? Link { get; init; }
	public string? TargetKind { get; init; }
	public string[]? TargetStatuses { get; init; }
	// Mirrors MethodologyLinkConstraintDef.Description: free-form prose (spec methodology-
	// primitive-descriptions). Null = none.
	public string? Description { get; init; }
}

// One declared transition effect of a kind: when a node of this kind ENTERS status `on`
// (default), or LEAVES it (`onLeave: true`, Effect.onLeave), linked nodes over relation kind
// `link` in `direction` (incoming|outgoing) are set to status `set`; `onlyFrom` optionally
// restricts the effect to linked nodes currently in that status. `set` omitted = a pure
// edge-consumption effect (no status propagated to the linked node).
public sealed record MethodologyEffectInput
{
	public string? On { get; init; }
	public string? Link { get; init; }
	public string? Direction { get; init; }
	public string? Set { get; init; }
	public string? OnlyFrom { get; init; }
	public bool OnLeave { get; init; }
	// Mirrors MethodologyTransitionEffectDef.Description: free-form prose (spec methodology-
	// primitive-descriptions). Null = none.
	public string? Description { get; init; }
}

// A project-declared relation kind: a free semantic edge, no FSM effects. `slug` must not
// collide with a builtin process/neutral kind. `category` is neutral|process (default
// neutral); `direction` optionally declares the stored edge's orientation.
public sealed record MethodologyLinkKindInput
{
	public string? Slug { get; init; }
	public string? Description { get; init; }
	// Mirrors MethodologyLinkKindDef.Category: neutral|process (default neutral when null).
	// v1 declaration only — parsed to the LinkCategory enum by MethodologyWire.
	public string? Category { get; init; }
	// Mirrors MethodologyLinkKindDef.Direction: the stored-edge orientation. Null = none.
	public MethodologyLinkDirectionInput? Direction { get; init; }
}

// The stored-edge orientation of a declared relation kind (mirrors MethodologyLinkDirectionDef):
// fromKind/toKind constrain the node kind at each end of relations.from→to; null = unconstrained.
public sealed record MethodologyLinkDirectionInput
{
	public string? FromKind { get; init; }
	public string? ToKind { get; init; }
	public string? Label { get; init; }
}

// A declared tag namespace (axis) for the project's definition-resolved boards.
public sealed record MethodologyTagAxisInput
{
	public string? Namespace { get; init; }
	public string? Description { get; init; }
}

// One state machine shared by every type slug in `types` (the tasks_workflow block
// shape). Convention: statuses[0] is the initial status.
public sealed record MethodologyWorkflowInput
{
	public string[]? Types { get; init; }
	// The READ-BACK of the positional convention (MethodologyWorkflowDef.Initial => Statuses[0].Slug):
	// MethodologyWorkflowBlockView EMITS `initial`, so every tasks_methodology_template_get /
	// rules_get / utility_get document carries it, and the documented round trip ("same document
	// shape ... copyable into def_upsert/template_upsert without reshaping", TasksTools) sends it
	// straight back. It used to be DROPPED silently by the binder; under the MCP options'
	// UnmappedMemberHandling.Disallow a member with no home is a REFUSAL, so this field has to exist
	// here or that paste stops working.
	//
	// VALIDATED, not honoured (MethodologyWire.ParseWorkflow). Honouring it as the initial-status
	// DECLARATION would mean reordering the caller's `statuses` behind its back to keep
	// Statuses[0] == Initial — silently mutating one half of the document to satisfy the other,
	// the exact quiet data-shift the strictness exists to end, and it would undo a deliberate
	// reorder for a caller who moved a status to the front and left the stale `initial` in place.
	// So a disagreement is the caller's bug and is NAMED. Blank/omitted = not declared: a
	// hand-authored document never has to write it, and nothing about the convention changes.
	public string? Initial { get; init; }
	public MethodologyStatusInput[]? Statuses { get; init; }
	public MethodologyTransitionInput[]? Transitions { get; init; }
}

// A workflow status: `kind` is open|terminalok|terminalcancel (default open), the same
// string vocabulary tasks_workflow answers with; `name` defaults to the slug.
public sealed record MethodologyStatusInput
{
	public string? Slug { get; init; }
	public string? Name { get; init; }
	public string? Kind { get; init; }
	// Mirrors WorkflowStatus.Description: free-form prose (spec methodology-primitive-
	// descriptions). Null = none.
	public string? Description { get; init; }
}

// A directed FSM edge. `preconditionArtifact` names a comment-artifact tag (e.g.
// "spec_plan") that must exist on the node before the transition — modeled here,
// enforced by the engine task. `enforceApproval` (only with requiresApproval) declares
// the approval gate as server-BLOCKED rather than owner-only by convention; `checklist`
// is free-text conditions to confirm before the transition (guide-rendered, not enforced).
//
// `requiresReason`/`preconditionArtifact`/`enforceApproval` are the LEGACY shape (kept so an
// already-stored document keeps round-tripping unedited). New authoring should use
// `requiredArtifacts`/`enforce` instead (spec methodology-gate-strictness) — declaring the
// gate (approval + which comment artifacts) separately from its server-strictness; `reason` is
// just an artifact with slug "reason", inline:true — there is no separate reason gate. The
// service rejects mixing both shapes on one transition.
public sealed record MethodologyTransitionInput
{
	public string? From { get; init; }
	public string? To { get; init; }
	public bool RequiresApproval { get; init; }
	public bool RequiresReason { get; init; }
	public string? PreconditionArtifact { get; init; }
	public bool EnforceApproval { get; init; }
	public string[]? Checklist { get; init; }
	// Mirrors MethodologyTransitionDef.Description: free-form prose (spec methodology-
	// primitive-descriptions). Null = none.
	public string? Description { get; init; }
	// Mirrors MethodologyTransitionDef.RequiredArtifacts: the unified requiredArtifacts gate
	// (schema v2, spec methodology-gate-strictness). Null/omitted = declare via the legacy
	// fields instead (or no artifact gate).
	public MethodologyRequiredArtifactInput[]? RequiredArtifacts { get; init; }
	// Mirrors MethodologyTransitionDef.Enforce: this transition's strictness override. Null =
	// fall through to the defaults (approval → the definition's strictMode; artifacts → always
	// hard).
	public MethodologyGateEnforcementInput? Enforce { get; init; }
}

// Mirrors RequiredArtifactDef 1:1 (see the parity note on MethodologyKindInput above).
public sealed record MethodologyRequiredArtifactInput
{
	public string? Slug { get; init; }
	public bool Inline { get; init; }
}

// Mirrors GateEnforcementDef 1:1 — both fields independently nullable (null = no opinion,
// fall through to that field's own default; see GateEnforcementDef).
public sealed record MethodologyGateEnforcementInput
{
	public bool? Approval { get; init; }
	public bool? Artifacts { get; init; }
}

// One entry of the `migration` argument of tasks_methodology_rules_upsert: declared value
// repairs for live nodes on boards of `kind` that a definition change would otherwise
// strand. A mapping applies ONLY where a node's current value is invalid under the NEW
// resolution — a valid value is never rewritten (declarative repair, not bulk rename).
public sealed record MethodologyMigrationInput
{
	// The board-kind slug the mappings repair (a kind of the new definition, a builtin
	// kind, or the slug of a kind the new definition DROPPED — its boards keep the slug).
	public string? Kind { get; init; }
	public MethodologyValueMapInput[]? Types { get; init; }
	public MethodologyValueMapInput[]? Statuses { get; init; }
}

// One {from,to} value repair; `to` must be valid under the new resolution.
public sealed record MethodologyValueMapInput
{
	public string? From { get; init; }
	public string? To { get; init; }
}

// The `sort` argument of tasks_search: `by` names the axis (priority|created|updated|title|
// relevance — relevance only with a query), `desc` flips the direction (ignored for
// relevance, whose fused order is already most-relevant-first).
public sealed record SortInput
{
	public string? By { get; init; }
	public bool Desc { get; init; }
}

// One transcript message as submitted to session_append — the same {role, content} shape the
// snapshot stores and the REST ndjson push sends; the server assigns the ordinal.
public sealed record SessionMessageDto
{
	public string? Role { get; init; }
	public string? Content { get; init; }

	// A message body carried by REFERENCE (work/write-body-by-reference). NAMED `contentRef`, not
	// `bodyRef`: this surface's text field is `content`, and a parameter must name the field it
	// replaces. PER MESSAGE, not per call — a batch may carry one huge referenced message (a
	// subagent's transcript, a captured command output) beside several ordinary inline ones.
	// Mutually exclusive with `content`; sending both is a refusal for the whole call.
	public string? ContentRef { get; init; }
}

// One item of a config_binding_upsert batch (typed array, like TaskNodeInput/CommentItemInput).
// A binding is identified by (path, normalized tag SET) within the workspace — a PUT: an active
// twin with the same (path, tagset) is superseded. There is NO version watermark (config rows are
// immutable, keyed by an auto-increment id; a change mints a new row), so this DTO carries no
// `version`/`id`. `kind`: 'Plain' (default) or 'Secret' (value stored encrypted, never returned).
public sealed record ConfigBindingItemInput
{
	public string? Path { get; init; }
	public string? Tags { get; init; }
	public string? Value { get; init; }
	public string? Kind { get; init; }
}

// One item of a comments_upsert batch (typed array, like TaskNodeInput/MemoryEntryInputDto).
// `id` null/absent ⇒ CREATE (needs `node` — a node reference: a slug key or a 32-hex NodeId,
// both accepted — plus `author`; `parentId` = a COMMENT id, NOT a node reference
// makes it a reply); `id` present ⇒ PATCH `body`/`tags` of that comment under the `version`
// watermark. `tags`: null = leave as-is on an edit, [] clears, a list replaces the set.
public sealed record CommentItemInput
{
	public string? Id { get; init; }
	public string? Node { get; init; }
	public string? ParentId { get; init; }
	public string? Author { get; init; }
	public string? Body { get; init; }
	// Point edit of the body: a list of {old, new} substitutions applied IN ORDER against the
	// node's CURRENT body, so the call costs the size of the CHANGE rather than the size of the
	// node (spec/write-cost-follows-change). Mutually exclusive with `body`. An `old` that matches
	// zero times, or more than once, REFUSES the whole write through conflicts[] — never a
	// first-match guess, never a partial application.
	public IReadOnlyList<FragmentEditDto>? Fragment { get; init; }
	// A body that ALREADY EXISTS as a file, carried by REFERENCE instead of retyped into this call
	// (work/write-body-by-reference). Upload the file to POST /api/blobs/{projectKey} — a RAW body,
	// not JSON, which is the point: the text never passes through the model's output and so never
	// pays the \uXXXX escaping tax that truncates long Cyrillic calls. Send the `ref` it returns
	// here, verbatim. ONE-SHOT: consumed by the write that references it (a refused write does NOT
	// consume it — retry with the same ref), and expiring 24h after upload otherwise.
	// Mutually exclusive with `body` AND with `fragment`: two of them in one item is a REFUSAL
	// through conflicts[], never a silent precedence — the same rule `body` vs `fragment` follows.
	public string? BodyRef { get; init; }
	public IReadOnlyList<string>? Tags { get; init; }
	public long Version { get; init; }
	// comment-slug-and-refs: the comment's human-readable address WITHIN ITS OWNING NODE — the thing
	// that makes a segment quotable from a body as `[[#slug]]` and survives a reorder, which a
	// position never does. Optional everywhere: omitted leaves a create without one and a patch with
	// whatever it already had. Unique among the node's active comments (two comments under DIFFERENT
	// nodes may share a slug), shaped `[a-z][a-z0-9_-]{0,99}`, and WRITE-ONCE — see CommentItem.Slug
	// for why a set slug is never re-pointed.
	public string? Slug { get; init; }
}

// One item of a relations_create batch. `from`/`to` are each a node REFERENCE — a slug key or a
// 32-hex NodeId (both accepted). The `fromNodeId`/`toNodeId` item aliases were REMOVED
// (drop-legacy-aliases); they are now unknown members and are REJECTED, not silently dropped.
// The tool's SINGLE form was renamed to match (`fromNodeId`/`toNodeId` -> `from`/`to`), so the two
// forms now share ONE vocabulary: a node-reference parameter never carries an Id/Key/Slug suffix,
// because it accepts either spelling. `FromNodeId`/`ToNodeId` survive only on the RESPONSE, where
// the value really is always a NodeId and the suffix is therefore honest.
public sealed record RelationCreateItemInput
{
	public string? Kind { get; init; }
	public string? From { get; init; }
	public string? To { get; init; }
}

// An entry as submitted to memory_upsert. Mirrors EXACTLY the fields the old JsonElement parser
// (MemoryTools.ParseEntries) accepted, including the `deleted:true` soft-delete marker.
public sealed record MemoryEntryInputDto
{
	public string? Key { get; init; }
	public long Version { get; init; }
	public string? Type { get; init; }
	public string? Description { get; init; }
	public string? Body { get; init; }
	// Point edit of the body: a list of {old, new} substitutions applied IN ORDER against the
	// node's CURRENT body, so the call costs the size of the CHANGE rather than the size of the
	// node (spec/write-cost-follows-change). Mutually exclusive with `body`. An `old` that matches
	// zero times, or more than once, REFUSES the whole write through conflicts[] — never a
	// first-match guess, never a partial application.
	public IReadOnlyList<FragmentEditDto>? Fragment { get; init; }
	// A body that ALREADY EXISTS as a file, carried by REFERENCE instead of retyped into this call
	// (work/write-body-by-reference). Upload the file to POST /api/blobs/{projectKey} — a RAW body,
	// not JSON, which is the point: the text never passes through the model's output and so never
	// pays the \uXXXX escaping tax that truncates long Cyrillic calls. Send the `ref` it returns
	// here, verbatim. ONE-SHOT: consumed by the write that references it (a refused write does NOT
	// consume it — retry with the same ref), and expiring 24h after upload otherwise.
	// Mutually exclusive with `body` AND with `fragment`: two of them in one item is a REFUSAL
	// through conflicts[], never a silent precedence — the same rule `body` vs `fragment` follows.
	public string? BodyRef { get; init; }

	// Tags as an ARRAY of tag strings (like tasks): null = omit (PATCH: keep the current
	// set), [] = explicit clear, a non-empty list REPLACES the set.
	public IReadOnlyList<string>? Tags { get; init; }

	public string? Metadata { get; init; }
	public string? PrevKey { get; init; }

	// Soft-delete marker: { key, deleted:true } closes the active entry (optional version baseline).
	public bool Deleted { get; init; }
}

// One {old, new} substitution of a `fragment` list (work/write-fragment-patch). Both fields are
// REQUIRED: `old` must occur EXACTLY once in the current text, and `new` must be present (send ""
// to delete the matched text). NOTE — McpUnknownParameterFilter walks the top level and ONE hop
// into batch items, so it does NOT police the fields of this second-hop object; a misspelt `old`/
// `new` therefore arrives as null and is refused by FragmentPatch, not by the filter.
public sealed record FragmentEditDto
{
	public string? Old { get; init; }
	public string? New { get; init; }

	// Wire shape -> the core primitive. Nulls are carried through UNCHANGED rather than defaulted
	// to "": FragmentPatch refuses a null `old`/`new` with a message naming the field, and that
	// refusal is the only thing standing between a second-hop typo and a silent deletion.
	public static IReadOnlyList<FragmentEdit>? ToCore(IReadOnlyList<FragmentEditDto>? dtos) =>
		dtos?.Select(d => new FragmentEdit(d.Old, d.New)).ToList();
}

// ── agent_def_upsert: ONE typed nested document ──────────────────────────────────────────────
//
// work/agent-def-upsert-typed-and-merge-by-role. Two changes ride on these records and they are
// the same change:
//
//   TYPED — `definition` used to be a bare `JsonElement` with `[McpJsonShape("object")]`, which
//   stamps `"type":"object"` and nothing else: no `properties`, no `required`, so the document's
//   real structure existed only in the prose of a description. That violates spec/typed-mcp-inputs
//   («голый JsonElement запрещён») and left a mistyped field name to bind a default — under
//   full-replace, a silent DELETE of stored data.
//
//   MERGE — the roles a call carries are the roles it CHANGES. A role absent from `roles` is
//   untouched, so editing one role costs one role (spec/write-cost-follows-change) instead of the
//   10 520 B the six-role roster measured at.
//
// SHAPE: this MIRRORS what agent_def_get emits — `{ name, roles:[{ slug, tier,
// requiredCapabilities, spawn?:{allowed, allowedRoles}, escalation?:{available, targets},
// notes? }] }` — so read → edit → write is a paste, not a reshaping exercise. An earlier draft of
// this card destructured the document into `roles[]` + `name` and FLATTENED the role
// (`spawn:{allowed}` → `spawnAllowed`) to stay inside McpUnknownParameterFilter's one-hop walk.
// That constraint is GONE (work/mcp-unmapped-member-disallow: the MCP serializer options carry
// UnmappedMemberHandling.Disallow, so an unmapped member is refused BY THE TYPE at any depth), and
// the shape it forced does not scale — every new field would become another top-level parameter.
// Do not re-flatten citing the filter.
//
// There is no `model` member anywhere in this tree, and there never will be: model binding is
// LOCAL, not part of a portable definition. `roles[].model` is refused by name by the binder, and
// AgentDefinitionJson.Parse re-checks the merged document, so it cannot reach the store by any
// route (REST included).

// spawn, as agent_def_get emits it. BOTH halves nullable: `spawn:{allowedRoles:[…]}` keeps the
// stored `allowed`, and `spawn:{allowed:false}` keeps the stored list. Omit `spawn` entirely and
// the block is not this call's business at all.
public sealed record AgentDefSpawnInput(
	bool? Allowed = null,
	IReadOnlyList<string>? AllowedRoles = null);

// escalation, same shape and the same either-half rule.
public sealed record AgentDefEscalationInput(
	bool? Available = null,
	IReadOnlyList<string>? Targets = null);

// One role's edit. `slug` is the IDENTITY — which stored role this addresses; an unknown slug
// APPENDS a role (create and edit are one verb, as in tasks_upsert). Every other field is optional
// and means "leave whatever the stored role has" (omit=keep, the surface-wide convention).
//
// A positional record on purpose: STJ's schema exporter marks a ctor parameter `required` when it
// has no default, so `slug` lands in the published `required` array without needing
// [McpRequiredMember] — which would not have worked here anyway, since McpOutputSchema's
// WithRequiredMembers walks ONE level (parameter, or its array `items`) and this role sits under
// `definition.roles`. The CLR type stays non-nullable-but-unenforced, so a missing slug reaches the
// tool's own sentence rather than a raw binder error.
//
// `deleted` is input-only — agent_def_get never emits it, so its presence here does not break the
// paste-back round trip in either direction.
public sealed record AgentDefRoleInput(
	string Slug,
	string? Tier = null,
	IReadOnlyList<string>? RequiredCapabilities = null,
	AgentDefSpawnInput? Spawn = null,
	AgentDefEscalationInput? Escalation = null,
	string? Notes = null,
	bool Deleted = false);

// The document. `roles` carries only the roles being CHANGED; `name` is optional and omit=keep,
// so a paste of a read document (which always carries `name`) is a no-op on the name.
public sealed record AgentDefDocumentInput(
	IReadOnlyList<AgentDefRoleInput> Roles,
	string? Name = null);
