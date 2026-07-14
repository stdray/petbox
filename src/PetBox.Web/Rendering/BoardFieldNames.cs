namespace PetBox.Web.Rendering;

// The node properties a board's view MAY show, and their dialog labels (spec board-view-fields):
// slug, type, status, priority, tags, updatedAt, delivery, blockedBy, body. Purely a PRESENTATION
// vocabulary — unlike BoardViewModeNames (PetBox.Tasks.Workflow), nothing here is methodology
// data: no MethodologyKindDef field stores a per-kind default set, so there is no "materialized at
// instance-creation, never reaches an existing instance" trap to guard (board-view-defaults-not-
// applied-existing-instances) — BoardFieldConfig.Default (below, same file's sibling) computes the
// default straight from the (view mode, MethodologyRuntime.DeliveryOf) already in hand on every
// request, so a methodology change or a runtime code change both take effect immediately for every
// existing board, nothing to migrate.
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
	];

	public static readonly IReadOnlyList<string> All = Options.Select(o => o.Key).ToList();

	public static bool IsKnown(string? name) => name is not null && All.Contains(name, StringComparer.OrdinalIgnoreCase);
}
