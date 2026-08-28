using PetBox.Core.Settings;

namespace PetBox.Core.Search;

// The rerank INPUT SIZE CAPS applied on the ONE common rerank path (SearchService.RankPoolAsync) —
// deliberately NOT three copies in the memory/session/tasks resolvers (bug
// rerank-oversize-falls-through-both-legs): the resolvers hand SearchService the RAW candidate text
// (sessions: message content; memory: Description+"\n"+Body; tasks: Name+"\n"+Body / comment Body) and
// the raw query, unchanged from before this fix — truncation happens once, right before the cross-
// encoder call, so a fourth future caller of RankPoolAsync inherits the cap for free instead of having
// to remember to add it.
//
// Mirrors RerankCandidateBudget's shape/pattern on purpose (declared number, settings-resolved,
// System -> Workspace -> Project override, a caller with no ISettingsResolver gets the same honest
// default as one that resolved and found no override). See RerankTruncationSettings for why 6000/2000
// are the defaults.
public sealed record RerankInputTruncation
{
	public int DocumentChars { get; init; } = 6000;
	public int QueryChars { get; init; } = 2000;

	public static RerankInputTruncation FromSettings(RerankTruncationSettings settings) => new()
	{
		DocumentChars = settings.DocumentChars,
		QueryChars = settings.QueryChars,
	};

	// THE production door, mirroring RerankCandidateBudget.ResolveAsync: every SearchService call site
	// resolves through here instead of constructing RerankInputTruncation directly, so a Project-scope
	// override actually lands. `settingsResolver` nullable for the same reason every other optional
	// collaborator here is — a hand-constructed test/adapter with no DI graph gets an honest, unwired
	// default rather than a null-ref.
	public static async Task<RerankInputTruncation> ResolveAsync(
		ISettingsResolver? settingsResolver, string projectKey, CancellationToken ct = default)
	{
		if (settingsResolver is null) return new RerankInputTruncation();
		var settings = await settingsResolver.GetAsync<RerankTruncationSettings>(Scope.Project, projectKey, ct);
		return FromSettings(settings);
	}

	// Truncate, never drop: an oversized document/query degrades ranking quality for that one
	// candidate/query slightly (the cross-encoder sees a prefix instead of the whole text) rather than
	// losing the ENTIRE precision pass to an upstream size-limit refusal — that trade is the whole point
	// of this type. A non-positive cap is treated as "no limit" (defensive; the settings default is
	// always positive, but a misconfigured override must not throw or silently empty every document).
	public string TruncateDocument(string text) => Truncate(text, DocumentChars);
	public string TruncateQuery(string text) => Truncate(text, QueryChars);

	static string Truncate(string s, int cap) => cap > 0 && s.Length > cap ? s[..cap] : s;
}
