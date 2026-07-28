namespace PetBox.Web.Pages.Shared;

// ui-search-shared-panel: the three consumers this card names — Search.cshtml (the cross-scope
// locator), Sessions.cshtml, MemoryStore.cshtml — each had their OWN copy of the free-text query
// box, the page-size dropdown and the single-line boundary/notice alert. Card's own drawn
// boundary: entity ROW rendering stays per-entity (_TaskTable already does this for tasks;
// sessions/memory keep their own card/list-item markup) — only the panel input and the
// result-chrome pieces that are byte-for-byte identical in SHAPE become shared, parameterized by
// what differs (placeholder, testid, message text). Sort/filter controls and a ranking-mode
// selector are NOT unified here: their vocabulary genuinely differs per entity (session sort has
// updated/created/length + a separate asc/desc toggle; memory sort folds relevance in with no
// separate direction control) and no page currently renders a ranking-mode control at all (it's a
// global /ui/me/preferences setting) — forcing a shared control over that would be inventing
// behavior, not preserving it. This is a deliberate, disclosed scope line, not an oversight.

// The free-text query input every search surface renders (Search.cshtml's lone box, Sessions' and
// MemoryStore's `q` field). `Name` defaults to "q", the query-string key every consumer already
// binds. Type/CssClass/Autofocus default to Sessions'/MemoryStore's own shared shape
// (type="search", input-sm, no autofocus) — Search.cshtml (the one page that differs: plain
// type="text", no input-sm sizing, autofocus on the one-box locator form) passes its own values
// rather than losing them to a forced-common style.
public sealed record SearchQueryBoxModel(
	string? Value, string Placeholder, string TestId, string Name = "q",
	string Type = "search", string CssClass = "input input-bordered input-sm flex-1 min-w-[16rem] max-w-md",
	bool Autofocus = false);

// The page-size dropdown (spec ui-search-page-position-and-size): identical
// PageSizeOptions.Allowed loop, previously copy-pasted between Sessions.cshtml and
// MemoryStore.cshtml. Search.cshtml is NOT a consumer — the cross-scope locator has no per-page
// depth knob at all (PageSizeOptions.cs's own header explains why).
public sealed record SearchPageSizeSelectModel(int CurrentSize, string TestId);

// One single-line result-chrome alert (spec result-set-pageable requirement 2: a boundary must be
// STATED, never implied) — the SAME `alert alert-{severity} mb-3 text-sm` shape Search.cshtml's
// locator-ceiling notice, Sessions' cursor-reset/pool-boundary/not-distilled notices and
// MemoryStore's cursor-error/pool-boundary notices all rendered independently before this card.
// Message text stays entity-specific (a caller-supplied string) — only the wrapper markup shares.
// CssClass carries whatever spacing/sizing differed between the original call sites (Search's and
// Sessions' notices were "mb-3 text-sm"; MemoryStore's were "mb-2" with no text-sm) — kept as a
// parameter rather than flattened to one value, so this extraction changes no page's rendered class list.
public sealed record SearchNoticeModel(string TestId, string Message, string Severity = "warning", string CssClass = "mb-3 text-sm");
