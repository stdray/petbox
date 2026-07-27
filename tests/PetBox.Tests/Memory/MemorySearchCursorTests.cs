using System.Security.Claims;
using LinqToDB;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Core.Search;
using PetBox.Core.Settings;
using PetBox.Memory.Data;
using PetBox.Memory.Services;
using PetBox.Web.Mcp;
using PetBox.Web.Mcp.Contract;

namespace PetBox.Tests.Memory;

// memory_search PAGINATION (work/search-results-pageable, spec result-set-pageable).
//
// memory_search had NO cursor at all — not a restricted one, none. The tail of a store was reachable
// only through memory_delta, i.e. the DELTA surface was standing in for a listing, which is a different
// question wearing the same clothes. These tests hold the new cursor to the same standard tasks_search's
// is held to, plus the two things that are memory's alone:
//
//   * THE CASCADE. A page is spliced from project ⊕ workspace, so the resume key must carry the SCOPE
//     as well as (store, key) — nothing less is unique — and the change stamp must cover EVERY
//     container, or a write to the far leg would silently reorder a walk the near leg certified.
//   * THE SAME THREE WORDS. `stop` must read "more" | "exhausted" | "pool-boundary" exactly as
//     tasks_search does. Three read surfaces answering in three shapes is what this work exists to end.
public sealed class MemorySearchCursorTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<MemoryDb> _factory;
	readonly MemoryService _memory;
	// Injected explicitly (production wires it as a singleton) so a test can OBSERVE whether a page
	// built a new pool or reused the stored one — the only end-to-end signal that "one rerank per pool"
	// is actually holding rather than merely intended.
	readonly SearchPoolCache _poolCache = new();

	public MemorySearchCursorTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-memcursor-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs); // seeds $system + $workspace projects
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_factory = new ScopedDbFactory<MemoryDb>(Path.Combine(_dir, "memory"), Scope.Project,
			c => new MemoryDb(MemoryDb.CreateOptions(c)), MemorySchema.Ensure);
		_memory = new MemoryService(new MemoryStore(_db.Factory(), _factory), poolCache: _poolCache);
	}

	public void Dispose()
	{
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	static IHttpContextAccessor Http()
	{
		var id = new ClaimsIdentity([new Claim("project", Proj), new Claim("scopes", "memory:read,memory:write")], "test");
		var ctx = new DefaultHttpContext { RequestServices = TestProjectCatalog.Services, User = new ClaimsPrincipal(id) };
		return new HttpContextAccessor { HttpContext = ctx };
	}

	static FeatureFlags Flags()
	{
		var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
		{
			["Features:Memory"] = "true",
		}).Build();
		return new FeatureFlags(cfg);
	}

	Task Remember(string text, string scope = "project") =>
		MemoryTools.RememberAsync(Http(), Flags(), _db.Factory().WorkspaceMemory(), _memory, text, scope: scope);

	Task<MemorySearchResultView> Search(
		string? q = null, string? scope = null, int? limit = null, string? cursor = null, string? store = null) =>
		MemoryTools.SearchAsync(Http(), Flags(), _db.Factory().WorkspaceMemory(), _memory, new NoopUsageRecorder(),
			q, scope, null, store, null, null, limit, null, false, null, cursor);

	// Walk to exhaustion, returning every (scope, key) in page order.
	static async Task<List<string>> WalkAsync(Func<string?, Task<MemorySearchResultView>> page)
	{
		var keys = new List<string>();
		string? cursor = null;
		for (var guard = 0; guard < 200; guard++)
		{
			var res = await page(cursor);
			keys.AddRange(res.Items.Select(i => i.Scope + "/" + i.Store + "/" + i.Key));
			if (res.NextCursor is null) return keys;
			cursor = res.NextCursor;
		}
		throw new InvalidOperationException("page walk did not terminate — nextCursor never went away");
	}

	async Task SeedSixProject()
	{
		foreach (var n in new[] { "one", "two", "three", "four", "five", "six" })
			await Remember($"deploy note {n} about the release pipeline");
	}

	// ── THE invariant: pages == the whole thing ──────────────────────────────────────────────

	[Fact]
	public async Task PageWalk_WithQuery_ConcatenatesToTheUnpagedSelection()
	{
		await SeedSixProject();

		var whole = (await Search(q: "deploy", limit: 100)).Items
			.Select(i => i.Scope + "/" + i.Store + "/" + i.Key).ToList();
		whole.Should().HaveCount(6);

		var paged = await WalkAsync(c => Search(q: "deploy", limit: 2, cursor: c));

		paged.Should().Equal(whole, "paging must change presentation only — never selection or order");
		paged.Should().OnlyHaveUniqueItems("a keyset seek re-serves nothing");
	}

	[Fact]
	public async Task PageWalk_PageSizeOfOne_StillCoversThePoolWithoutHoles()
	{
		await SeedSixProject();

		var whole = (await Search(q: "deploy", limit: 100)).Items.Select(i => i.Key).ToList();
		var paged = await WalkAsync(c => Search(q: "deploy", limit: 1, cursor: c));

		paged.Select(k => k.Split('/')[^1]).Should().Equal(whole);
	}

	[Fact]
	public async Task SecondWalk_OverUnchangedData_ReproducesTheFirstWalkExactly()
	{
		await SeedSixProject();

		var first = await WalkAsync(c => Search(q: "deploy", limit: 2, cursor: c));
		var second = await WalkAsync(c => Search(q: "deploy", limit: 2, cursor: c));

		second.Should().Equal(first);
	}

	// ── the cascade: both containers, one order, one stamp ───────────────────────────────────

	[Fact]
	public async Task PageWalk_AcrossTheScopeCascade_CoversBothContainersExactlyOnce()
	{
		// The merged order interleaves scopes by relevance, so a page boundary can fall INSIDE the
		// interleaving. The resume key carries the scope precisely so it still lands on one row.
		await Remember("deploy deploy deploy release");
		await Remember("a note that mentions deploy once");
		await Remember("deploy pipeline workspace side", scope: "workspace");
		await Remember("deploy deploy workspace strong", scope: "workspace");

		var whole = (await Search(q: "deploy", limit: 100)).Items
			.Select(i => i.Scope + "/" + i.Key).ToList();
		whole.Should().HaveCount(4);
		whole.Select(k => k.Split('/')[0]).Distinct().Should().HaveCountGreaterThan(1,
			"this test is only meaningful if both containers contributed");

		var paged = await WalkAsync(c => Search(q: "deploy", limit: 1, cursor: c));

		paged.Select(k => string.Join('/', k.Split('/')[0], k.Split('/')[^1])).Should().Equal(whole);
		paged.Should().OnlyHaveUniqueItems();
	}

	[Fact]
	public async Task Cursor_IsRefused_AfterAWriteToTheWORKSPACEContainer()
	{
		// The cascade's sharpest failure mode, and the reason the stamp covers every container rather
		// than just the near one: a workspace write reorders the MERGED pool, so a token certified only
		// against the project container would happily serve a page from a different ordering.
		await Remember("deploy deploy release");
		await Remember("deploy pipeline");
		await Remember("deploy workspace side", scope: "workspace");
		var first = await Search(q: "deploy", limit: 1);
		first.NextCursor.Should().NotBeNull();

		await Remember("deploy deploy deploy workspace newcomer", scope: "workspace");

		var act = () => Search(q: "deploy", limit: 1, cursor: first.NextCursor);

		await act.Should().ThrowAsync<ArgumentException>().WithMessage("*DIFFERENT query*");
	}

	[Fact]
	public async Task Cursor_IsRefused_AfterAWriteToTheProjectContainer()
	{
		await SeedSixProject();
		var first = await Search(q: "deploy", limit: 2);

		await Remember("deploy deploy deploy a brand new project note");

		var act = () => Search(q: "deploy", limit: 2, cursor: first.NextCursor);

		await act.Should().ThrowAsync<ArgumentException>().WithMessage("*DIFFERENT query*");
	}

	// ── one pool, one ranking pass ───────────────────────────────────────────────────────────

	[Fact]
	public async Task SecondPage_ReusesTheStoredPool_InsteadOfBuildingANewOne()
	{
		await SeedSixProject();

		var first = await Search(q: "deploy", scope: "project", limit: 2);
		_poolCache.Count.Should().Be(1, "page 1 materializes and stores the ranked pool");

		var second = await Search(q: "deploy", scope: "project", limit: 2, cursor: first.NextCursor);

		second.Items.Should().NotBeEmpty();
		_poolCache.Count.Should().Be(1, "page 2 must SERVE the stored pool, not rank a fresh one");
	}

	// ── the boundary is stated, in the same words as the other surfaces ──────────────────────

	[Fact]
	public async Task ExhaustedSelection_SaysExhausted_AndIssuesNoCursor()
	{
		await SeedSixProject();

		var res = await Search(q: "deploy", limit: 100);

		res.NextCursor.Should().BeNull();
		res.Stop.Should().Be("exhausted", "every matching entry was ranked and served — there genuinely is no more");
		res.PoolBoundaryHint.Should().BeNull();
	}

	[Fact]
	public async Task MidWalk_SaysMore_AndIssuesACursor()
	{
		await SeedSixProject();

		var res = await Search(q: "deploy", limit: 2);

		res.Stop.Should().Be("more");
		res.NextCursor.Should().NotBeNull();
	}

	[Fact]
	public async Task QueryResponse_AlwaysStatesWhyItStopped_AndDeclaresTheDepth()
	{
		await SeedSixProject();

		foreach (var pageSize in new[] { 1, 2, 5, 100 })
		{
			var res = await Search(q: "deploy", limit: pageSize);
			res.Stop.Should().BeOneOf("more", "exhausted", "pool-boundary");
			res.PoolLimit.Should().NotBeNull().And.BeGreaterThan(0);
		}

		(await Search(limit: 5)).Stop.Should().BeNull("a listing has no ranked pool, so it declares no pool stop");
	}

	[Fact]
	public async Task StopVocabulary_MatchesTasksSearch_Exactly()
	{
		// Not a tautology: the two adapters build the string through the SAME mapping on purpose, and a
		// future edit that "improves" one surface's wording in isolation is exactly what this catches.
		MemoryTools.StopWire(SearchPoolStop.More).Should().Be("more");
		MemoryTools.StopWire(SearchPoolStop.Exhausted).Should().Be("exhausted");
		MemoryTools.StopWire(SearchPoolStop.PoolBoundary).Should().Be("pool-boundary");
	}

	// ── the deliberate refusals ──────────────────────────────────────────────────────────────

	[Fact]
	public async Task Cursor_FromAQuery_IsRefused_AgainstADifferentQuery()
	{
		await SeedSixProject();
		await Remember("something else entirely about pipelines");
		var first = await Search(q: "deploy", limit: 2);

		var act = () => Search(q: "pipelines", limit: 2, cursor: first.NextCursor);

		await act.Should().ThrowAsync<ArgumentException>().WithMessage("*DIFFERENT query*");
	}

	[Fact]
	public async Task Cursor_FromAQuery_IsRefused_InListingMode()
	{
		await SeedSixProject();
		var first = await Search(q: "deploy", limit: 2);

		var act = () => Search(limit: 2, cursor: first.NextCursor);

		await act.Should().ThrowAsync<ArgumentException>().WithMessage("*DIFFERENT query*");
	}

	[Fact]
	public async Task Cursor_ThatIsNotAToken_IsRefused()
	{
		await SeedSixProject();

		var act = () => Search(q: "deploy", limit: 2, cursor: "not-a-token!!");

		await act.Should().ThrowAsync<ArgumentException>().WithMessage("*OPAQUE*");
	}

	[Fact]
	public async Task BodyLenAndLimit_MayVaryBetweenPages()
	{
		// They shape a page, not the sequence — binding them would reject valid walks for no gain.
		await SeedSixProject();

		var first = await MemoryTools.SearchAsync(Http(), Flags(), _db.Factory().WorkspaceMemory(), _memory,
			new NoopUsageRecorder(), "deploy", null, null, null, null, null, 1, 0, false, null, null);
		var second = await MemoryTools.SearchAsync(Http(), Flags(), _db.Factory().WorkspaceMemory(), _memory,
			new NoopUsageRecorder(), "deploy", null, null, null, null, null, 2, -1, false, null, first.NextCursor);

		second.Items.Should().HaveCount(2);
	}
}
