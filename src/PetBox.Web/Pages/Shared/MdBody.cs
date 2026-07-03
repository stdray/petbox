namespace PetBox.Web.Pages.Shared;

// View model for _MdBody.cshtml — the ONE markdown-body renderer shared by node bodies,
// comment bodies, and the edit preview. Enforces uniform typography: text-base, no opacity
// dimming, class `md-body`. Only layout-level extras (margins, borders, padding) may be
// passed via ExtraClasses — never font-size or opacity, so every surface renders markdown
// at the same size.
public sealed record MdBodyModel
{
	// Raw markdown source, server-rendered as plain text into the [data-md] element; the
	// client (ts/markdown.ts) hydrates it to sanitized HTML. "" for the JS-filled preview.
	public string Body { get; init; } = "";

	public required string TestId { get; init; }

	// Layout-only extra classes (e.g. "mt-3", or the preview's border/padding/min-h).
	public string ExtraClasses { get; init; } = "";

	// Inline style passthrough (e.g. the edit preview's display:none).
	public string? Style { get; init; }

	// The edit preview carries no data-md (it's filled imperatively by ts/nodeEdit.ts);
	// every rendered body opts in so ts/markdown.ts hydrates it on load.
	public bool DataMd { get; init; } = true;
}
