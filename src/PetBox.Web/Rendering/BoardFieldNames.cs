namespace PetBox.Web.Rendering;

// The node properties a board's view MAY show, and their dialog labels (spec board-view-fields):
// slug, type, status, priority, tags, updatedAt, delivery, blockedBy, body, recurrence, sessions
// (the last two added by recurrence-and-session-provenance-as-board-fields). Purely a PRESENTATION
// vocabulary — unlike BoardViewModeNames (PetBox.Tasks.Workflow), nothing here is methodology
// data: no MethodologyKindDef field stores a per-kind default set, so there is no "materialized at
// instance-creation, never reaches an existing instance" trap to guard (board-view-defaults-not-
// applied-existing-instances) — BoardFieldConfig.Default (below, same file's sibling) computes the
// default straight from the (view mode, MethodologyRuntime.DeliveryOf/IsObservationKind) already in
// hand on every request, so a methodology change or a runtime code change both take effect
// immediately for every existing board, nothing to migrate.
public static class BoardFieldNames
{
	public const string Slug = "slug";
	public const string Type = "type";
	public const string Status = "status";
	public const string Priority = "priority";
	public const string Tags = "tags";
	public const string UpdatedAt = "updatedAt";
	public const string Delivery = "delivery";
	public const string BlockedBy = "blockedBy";
	public const string Body = "body";
	// spec observation-recurrence-visible-on-card: the observation recurrence counter + last-seen
	// time (_ObservationRecurrenceBadge), promoted from an unconditional render to a togglable
	// field like every other property. Only ever meaningful on an observation-kind board
	// (BoardFieldConfig.Default gates its default on MethodologyRuntime.IsObservationKind); the
	// fields dialog disables the checkbox elsewhere with the same idiom Body's BodyUnavailable uses.
	public const string Recurrence = "recurrence";
	// spec node-session-provenance-visible-in-ui: the node's origin/touching session links
	// (_NodeSessionProvenanceBadge). Meaningful on every board kind (every node carries
	// OriginSessionId/OriginSessions), so unlike Recurrence it is never disabled in the dialog —
	// just off by default (BoardFieldConfig.Default) so it doesn't add noise nobody asked for.
	public const string Sessions = "sessions";

	// (key, dialog label) in the order the fields dialog renders its checkboxes — also the
	// canonical order BoardFieldConfig.Keys()/ToCsv() emit, so a saved csv and a freshly resolved
	// one compare equal when they carry the same set.
	public static readonly IReadOnlyList<(string Key, string Label)> Options =
	[
		(Slug, "Slug"),
		(Type, "Type"),
		(Status, "Status"),
		(Priority, "Priority"),
		(Tags, "Tags"),
		(UpdatedAt, "Updated"),
		(Delivery, "Delivery"),
		(BlockedBy, "Blocked by"),
		(Body, "Body"),
		(Recurrence, "Recurrence"),
		(Sessions, "Sessions"),
	];

	private static readonly IReadOnlyList<string> All = Options.Select(o => o.Key).ToList();

	// The CSV of every key this build KNOWS about, in canonical Options order — written into
	// BoardViewPreference.FieldsKnown alongside Fields on every explicit dialog Apply
	// (TaskBoard.cshtml.cs), so a saved preference can later tell "the owner saw this key and left
	// it unchecked" apart from "this key didn't exist yet when the preference was last saved"
	// (BoardFieldConfig.FromSaved's whole reason for existing — see its own header comment).
	public static readonly string AllCsv = string.Join(",", All);

	// The vocabulary as it stood BEFORE FieldsKnown existed — the nine keys every saved preference
	// written by an older build necessarily had in front of the owner. A null/empty FieldsKnown
	// means exactly "saved by such a build", not "the owner knew nothing": treating it as an empty
	// known-set would throw away a customization the owner really did make (their unchecked Tags
	// would silently come back on), so FromSaved substitutes THIS list instead. Frozen on purpose —
	// a key added later belongs in Options/AllCsv above and must NOT be appended here, or it would
	// read as "already seen and deliberately unchecked" on every pre-FieldsKnown preference, which
	// is the precise failure this whole mechanism exists to prevent.
	public static readonly string LegacyKnownCsv = string.Join(",",
		[Slug, Type, Status, Priority, Tags, UpdatedAt, Delivery, BlockedBy, Body]);

	public static bool IsKnown(string? name) => name is not null && All.Contains(name, StringComparer.OrdinalIgnoreCase);
}
