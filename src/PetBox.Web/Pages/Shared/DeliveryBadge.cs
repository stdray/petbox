namespace PetBox.Web.Pages.Shared;

// The ONE spec-delivery-badge rule, shared by the board group headers (roll-up), the board card
// (_PlanNodeCard) and the node detail page (TaskBoardNode) so the delivery→colour mapping lives in
// one place. Muted (outline) palette — delivery is context, not an alert (spec-board-status-noise
// #11). Size/title/testid vary per surface (a group roll-up vs a node's own delivery) and are
// passed in; a null TestId omits the attribute (the group header carries none).
public sealed record DeliveryBadgeModel(string Delivery)
{
	public string Size { get; init; } = "badge-sm";
	public string Title { get; init; } = "delivery — computed from linked tasks";
	public string? TestId { get; init; } = "node-delivery";

	public string CssClass => Classify(Delivery);

	static string Classify(string delivery) => delivery switch
	{
		"done" => "badge-success badge-outline",
		"in_progress" => "badge-warning badge-outline",
		"done_with_defects" => "badge-error badge-outline",
		_ => "badge-ghost", // not_started
	};
}
