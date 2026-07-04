namespace PetBox.Web.Pages.Shared;

// View model for _MdBody.cshtml — the ONE markdown-body renderer shared by node bodies,
// comment bodies, and the edit preview. Enforces uniform typography: text-base, no opacity
// dimming, class `md-body`. Only layout-level extras (margins, borders, padding) may be
// passed via ExtraClasses — never font-size or opacity, so every surface renders markdown
// at the same size.
public sealed record MdBodyModel
{
	// Raw markdown source. On READ surfaces it is rendered to sanitized HTML on the SERVER
	// (IMarkdownRenderer) and emitted as real markup so the initial DOM is reader-view detectable.
	// "" for the JS-filled edit preview.
	public string Body { get; init; } = "";

	public required string TestId { get; init; }

	// Layout-only extra classes (e.g. "mt-3", or the preview's border/padding/min-h).
	public string ExtraClasses { get; init; } = "";

	// Inline style passthrough (e.g. the edit preview's display:none).
	public string? Style { get; init; }

	// When true (default), Body is rendered to sanitized HTML on the server and emitted directly.
	// When false, the element is left empty for the live edit-preview, which ts/nodeEdit.ts fills
	// client-side (ts/markdown.ts) as the user types.
	public bool ServerRender { get; init; } = true;
}
