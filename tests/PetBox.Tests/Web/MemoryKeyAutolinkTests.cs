using System.Security.Claims;
using LinqToDB;
using PetBox.Core.Auth;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Memory.Contract;
using PetBox.Memory.Data;
using PetBox.Memory.Services;
using PetBox.Web.Pages.Shared;
using PetBox.Web.Rendering;

namespace PetBox.Tests.Web;

// memory-key-mention-link, RENDER half: a memory-entry key mentioned in a body becomes a link WHEN
// the caller resolved it (the renderer never touches the DB — it consumes a prebuilt key→target
// map, the third card in the same AST pass as commit hashes and `[[slug]]` mentions). Anything the
// caller could not resolve unambiguously is simply absent from the map and stays literal.
public sealed class MemoryKeyRenderTests
{
	const string Key = "m-0123456789abcdef0123456789abcdef";
	const string AcKey = "ac-0123456789ab";
	const string Url = "/ui/$system/$system/memory/notes?key=m-0123456789abcdef0123456789abcdef#m-0123456789abcdef0123456789abcdef";
	const string CommitTemplate = "https://github.com/user/repo/commit/{sha}";

	static readonly IMarkdownRenderer R = new MarkdownRenderer();

	static IReadOnlyDictionary<string, NodeRefTarget> Map(params (string Key, string Url, string? Title)[] entries)
		=> entries.ToDictionary(e => e.Key, e => new NodeRefTarget(e.Url, e.Title), StringComparer.Ordinal);

	static string Html(string md, IReadOnlyDictionary<string, NodeRefTarget>? map)
		=> R.RenderToHtml(md, null, null, map);

	[Fact]
	public void ResolvedKey_BecomesLink_TextIsTheKey()
	{
		var html = Html($"see {Key} for the rule", Map((Key, Url, "memory · project · notes")));
		html.Should().Contain($"<a href=\"{Url}\"");
		html.Should().Contain("title=\"memory · project · notes\"");
		html.Should().Contain($">{Key}</a>");
	}

	[Fact]
	public void ResolvedAutocaptureKey_BecomesLink()
	{
		var html = Html($"per {AcKey}", Map((AcKey, $"/ui/a/b/memory/autocaptured?key={AcKey}#{AcKey}", null)));
		html.Should().Contain($">{AcKey}</a>");
	}

	// NOT FOUND and AMBIGUOUS are the same thing to the renderer: the caller left the key out of the
	// map (MemoryRefMap refuses both), so the key renders as plain text with no link.
	[Fact]
	public void UnresolvedOrAmbiguousKey_StaysLiteral()
	{
		var html = Html($"see {Key} here", Map(("m-ffffffffffffffffffffffffffffffff", Url, "T")));
		html.Should().NotContain("<a");
		html.Should().Contain(Key);
	}

	[Fact]
	public void Key_InsideCodeSpan_IsNotTouched()
	{
		var html = Html($"the key `{Key}` verbatim", Map((Key, Url, "T")));
		html.Should().NotContain("<a");
		html.Should().Contain($"<code>{Key}</code>");
	}

	[Fact]
	public void Key_InsideFencedCodeBlock_IsNotTouched()
	{
		var html = Html($"```\nmemory_get {Key}\n```", Map((Key, Url, "T")));
		html.Should().NotContain("<a");
		html.Should().Contain(Key);
	}

	[Fact]
	public void Key_InsideExistingLink_DoesNotDoubleLink()
	{
		var html = Html($"[{Key}](https://example.com/x)", Map((Key, Url, "T")));
		html.Should().Contain("href=\"https://example.com/x\"");
		html.Should().NotContain($"href=\"{Url}\"");
	}

	// No conflict with commit-hash autolinking: the hex tail of a key is preceded by `-`, which the
	// hash rule rejects — with a commit template active and NO memory map, the key stays fully
	// literal (not a half-linked "0123456789ab").
	[Fact]
	public void KeyIsNeverMistakenForACommitHash()
	{
		var plain = R.RenderToHtml($"see {Key} and {AcKey}", CommitTemplate);
		plain.Should().NotContain("<a");
		plain.Should().Contain(Key).And.Contain(AcKey);

		// …and with both contexts live, each transform links its own thing.
		var both = R.RenderToHtml($"fixed cc20e34 per {Key}", CommitTemplate, null, Map((Key, Url, "T")));
		both.Should().Contain("commit/cc20e34\"");
		both.Should().Contain($"href=\"{Url}\"").And.Contain($">{Key}</a>");
	}

	// Ordering with the node-ref card: an UNRESOLVED `[[…]]` mention wrapping a key stays literal in
	// full (the mention span wins), while a resolved key elsewhere in the same run still links.
	[Fact]
	public void KeyInsideUnresolvedNodeMention_StaysLiteral()
	{
		var html = R.RenderToHtml($"[[{Key}]] but {Key} links",
			null, Map(("other-node", "/n", "N")), Map((Key, Url, "T")));
		html.Should().Contain($"[[{Key}]]");
		html.Should().Contain($"href=\"{Url}\"");
	}

	[Fact]
	public void NoMap_OutputIdenticalToPlainPath()
	{
		var md = $"## Head\nsee {Key}\n\n- item";
		R.RenderToHtml(md, null, null, null).Should().Be(R.RenderToHtml(md));
		R.RenderToHtml(md, null, null, Map()).Should().Be(R.RenderToHtml(md));
		R.RenderToHtml(md, null, null, null).Should().NotContain("<a");
	}
}

// memory-key-mention-link, RESOLUTION half (over a real MemoryService + real stores): which keys
// earn a link. Batch: MemoryRefMap issues ONE resolution query per container (project + workspace),
// whatever the number of mentions — IMemoryService.ResolveKeysAsync resolves every candidate key in
// a single `Store IN (…) AND Key IN (…)` scan, because a container's stores share one file.
public sealed class MemoryRefMapTests : IDisposable
{
	const string Proj = "proj";
	const string Ws = "$system";
	const string WsContainer = "$workspace";

	const string ProjKey = "m-0123456789abcdef0123456789abcd01";
	const string WsKey = "m-0123456789abcdef0123456789abcd02";
	const string DupKey = "m-0123456789abcdef0123456789abcd03";
	const string CrossScopeKey = "m-0123456789abcdef0123456789abcd04";
	const string OpsKey = "m-0123456789abcdef0123456789abcd05";
	const string GhostKey = "m-0123456789abcdef0123456789abcd06";

	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<MemoryDb> _factory;
	readonly MemoryService _memory;

	public MemoryRefMapTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-memautolink-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs); // seeds the $system workspace + the $workspace memory container
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = Ws, Name = "P", Description = "" });
		_factory = new ScopedDbFactory<MemoryDb>(Path.Combine(_dir, "memory"), Scope.Project,
			c => new MemoryDb(MemoryDb.CreateOptions(c)), TestSchema.Memory);
		_memory = new MemoryService(new MemoryStore(_db.Factory(), _factory));
	}

	public void Dispose()
	{
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	async Task Put(string container, string store, string key) =>
		await _memory.UpsertAsync(container, store,
			[new MemoryEntryInput { Key = key, Version = 0, Type = "Project", Body = "b" }], []);

	// The default caller for every test below: no principal at all (AppliesTo(null) is false, the same
	// "ordinary caller" the workspace leg has always served) and no catalog — safe because
	// SandboxContainment.PermitsAsync never dereferences a null catalog unless the principal actually
	// carries the sandbox_only claim, which none of these do.
	async Task<IReadOnlyDictionary<string, NodeRefTarget>> Build(params string[] keys) =>
		await MemoryRefMap.BuildAsync(_memory, null, null, Ws, Proj, [string.Join(" and ", keys)], default);

	[Fact]
	public async Task ProjectScopeKey_ResolvesToItsStoreUrl()
	{
		await Put(Proj, "notes", ProjKey);

		var map = await Build(ProjKey);

		map.Should().ContainKey(ProjKey);
		map[ProjKey].Url.Should().Be($"/ui/{Ws}/{Proj}/memory/notes?key={ProjKey}#{ProjKey}");
	}

	// Workspace scope goes through its own UI entry — the reserved memory container project.
	[Fact]
	public async Task WorkspaceScopeKey_ResolvesToTheWorkspaceContainerUrl()
	{
		await Put(WsContainer, "canon", WsKey);

		var map = await Build(WsKey);

		map.Should().ContainKey(WsKey);
		map[WsKey].Url.Should().Be($"/ui/{Ws}/{WsContainer}/memory/canon?key={WsKey}#{WsKey}");
	}

	// Ambiguous WITHIN a scope: the same key in two stores of the project. No tie-break rule exists
	// that isn't a guess, so the mention keeps no link at all.
	[Fact]
	public async Task KeyInTwoStoresOfOneScope_IsAmbiguous_NoLink()
	{
		await Put(Proj, "notes", DupKey);
		await Put(Proj, "decisions", DupKey);

		(await Build(DupKey)).Should().NotContainKey(DupKey);
	}

	// Ambiguous ACROSS scopes: the same key in the project and in the workspace container.
	[Fact]
	public async Task KeyInBothScopes_IsAmbiguous_NoLink()
	{
		await Put(Proj, "notes", CrossScopeKey);
		await Put(WsContainer, "canon", CrossScopeKey);

		(await Build(CrossScopeKey)).Should().NotContainKey(CrossScopeKey);
	}

	// A SENSITIVE store is invisible to this path: a key that lives only there resolves to nothing,
	// so no automatic link into it can ever be built.
	[Fact]
	public async Task KeyInSensitiveStore_GetsNoLink()
	{
		await Put(Proj, "ops", OpsKey);

		MemoryStores.IsSensitive("ops").Should().BeTrue();
		(await Build(OpsKey)).Should().NotContainKey(OpsKey);

		// …and the raw resolution door refuses it too (the filter lives at the service, not the UI).
		(await _memory.ResolveKeysAsync(Proj, [OpsKey], default)).Should().NotContainKey(OpsKey);
	}

	[Fact]
	public async Task UnknownKey_GetsNoLink()
		=> (await Build(GhostKey)).Should().BeEmpty();

	// The batch shape: many mentions, one resolution per container — ResolveKeysAsync answers for
	// ALL keys at once (the assertion is that a multi-key ask is served in one call and every
	// resolvable key comes back; there is no per-key door on this path).
	[Fact]
	public async Task ManyKeys_ResolveInOneBatchPerContainer()
	{
		await Put(Proj, "notes", ProjKey);
		await Put(Proj, "notes", DupKey);
		await Put(WsContainer, "canon", WsKey);

		var resolved = await _memory.ResolveKeysAsync(Proj, [ProjKey, DupKey, GhostKey], default);
		resolved.Should().HaveCount(2);
		resolved[ProjKey].Should().BeEquivalentTo(["notes"]);

		var map = await Build(ProjKey, DupKey, WsKey, GhostKey);
		map.Should().HaveCount(3);
		map.Should().ContainKeys(ProjKey, DupKey, WsKey);
	}

	// ── SANDBOX CONTAINMENT ON THE DERIVED WORKSPACE LEG (work memoryrefmap-workspace-leg-ungated) ──
	//
	// PINNED HERE, NOT AT THE ROUTE, BECAUSE THE ROUTE CANNOT EXERCISE IT. Both pages that call
	// MemoryRefMap.BuildAsync (TaskBoard.cshtml.cs, TaskBoardNode.cshtml.cs) carry
	// [Authorize(Policy = "WorkspaceViewer")], and that policy names only the Cookie scheme — an
	// api-key identity (the only kind that ever carries the `sandbox_only` claim SandboxContainment
	// reads) is challenged to /Login before either page's handler runs at all (measured: 302,
	// byte-for-byte the unauthenticated redirect, not /AccessDenied — see the work card). A
	// WebApplicationFactory test that drove the real route with a sandboxOnly key would therefore only
	// ever prove the login redirect, never reach the predicate. BuildAsync is the lowest level that
	// actually calls SandboxContainment.PermitsAsync, so the gate is pinned by calling it directly with
	// a hand-built sandboxOnly ClaimsPrincipal — the same claim shape ApiKeyAuthenticationHandler
	// stamps, just without a live api-key request to carry it.
	const string SandboxLegKey = "m-0123456789abcdef0123456789abcd07";

	static ClaimsPrincipal SandboxOnlyPrincipal() =>
		new(new ClaimsIdentity([new Claim(ApiKeyAuthenticationHandler.SandboxOnlyClaim, "true")], "ApiKey"));

	[Fact]
	public async Task SandboxOnlyPrincipal_GetsNoWorkspaceLeg_EvenThoughTheKeyResolvesThere()
	{
		await Put(WsContainer, "canon", SandboxLegKey);

		// TestProjectCatalog.IsSandboxAsync is always false — exactly the real shape: a workspace's
		// shared-memory container is a real Projects row that is never itself a sandbox row
		// (SandboxContainment.cs), so the predicate denies the leg to a sandboxOnly caller.
		var map = await MemoryRefMap.BuildAsync(
			_memory, SandboxOnlyPrincipal(), TestProjectCatalog.Instance, Ws, Proj, [SandboxLegKey], default);

		map.Should().NotContainKey(SandboxLegKey,
			"a sandboxOnly principal must not receive the derived workspace-container leg — "
			+ "SandboxContainment.PermitsAsync denies it, the same predicate/suppress-the-leg shape "
			+ "MemoryApi.CanonAsync uses for the sibling call site");
	}

	// The differential control: same key, same seeded container, only the principal differs — proves
	// the gate is SandboxContainment-shaped (sandboxOnly-only) rather than a blanket suppression that
	// would silently break the workspace leg for every ordinary viewer too.
	[Fact]
	public async Task OrdinaryPrincipal_StillGetsTheWorkspaceLeg_SameKeySameSetup()
	{
		await Put(WsContainer, "canon", SandboxLegKey);

		var ordinary = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "u1")], "Cookies"));
		var map = await MemoryRefMap.BuildAsync(
			_memory, ordinary, null, Ws, Proj, [SandboxLegKey], default);

		map.Should().ContainKey(SandboxLegKey,
			"containment applies to a sandboxOnly principal only — an ordinary caller's workspace leg, "
			+ "and the null-catalog bare-construction posture BuildAsync keeps for it, must be untouched");
	}
}
