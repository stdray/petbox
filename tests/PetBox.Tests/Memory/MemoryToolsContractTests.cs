using System.Security.Claims;
using LinqToDB;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Memory.Data;
using PetBox.Memory.Services;
using PetBox.Web.Mcp;

namespace PetBox.Tests.Memory;

// Verifies the A4 taxonomy contract on the Memory MCP surface: type is required on
// upsert, list/search filter by type, tags normalise, and the store auto-vivifies
// on a cold upsert (A3, mirrored for memory).
[Collection("DataModule")]
public sealed class MemoryToolsContractTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<MemoryDb> _factory;
	readonly MemoryStore _store;
	readonly MemoryService _memory;

	public MemoryToolsContractTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-memtools-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_factory = new ScopedDbFactory<MemoryDb>(Path.Combine(_dir, "memory"), Scope.Project,
			c => new MemoryDb(MemoryDb.CreateOptions(c)), MemorySchema.Ensure);
		_store = new MemoryStore(_db, _factory);
		_memory = new MemoryService(_store);
	}

	public void Dispose()
	{
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		SqliteConnection.ClearAllPools();
		if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
	}

	[Fact]
	public async Task Upsert_WithoutType_IsRejected()
	{
		var http = Http("memory:read,memory:write");
		var entries = McpInputs.Entries(new object[]
		{
			new { key = "k", description = "d", body = "b" },
		});
		// The missing-type validation throws; McpErrorEnvelopeFilter renders {error} on the wire.
		await Assert.ThrowsAsync<ArgumentException>(() => MemoryTools.UpsertAsync(http, Flags(), _memory, Proj, "notes", entries));
	}

	[Fact]
	public async Task Upsert_AutoVivifies_NormalisesTags_AndFiltersByType()
	{
		var http = Http("memory:read,memory:write");

		// Cold upsert (no store_create) auto-creates the store; tags get normalised.
		var entries = McpInputs.Entries(new object[]
		{
			new { key = "go-style", type = "reference", description = "Go", body = "tabs", tags = "Go, STYLE ,go" },
			new { key = "prefers-tabs", type = "feedback", description = "tabs", body = "user likes tabs" },
		});
		await MemoryTools.UpsertAsync(http, Flags(), _memory, Proj, "notes", entries);
		(await _store.ExistsAsync(Proj, "notes")).Should().BeTrue();

		// Tags normalised: lowercased, trimmed, de-duped.
		var all = await MemoryTools.ListAsync(http, Flags(), _memory, Proj, "notes");
		var go = all.Entries.Single(e => e.Key == "go-style");
		go.Tags.Should().Be("go,style");
		go.Type.Should().Be("Reference");

		// Type filter narrows the listing.
		var feedback = await MemoryTools.ListAsync(http, Flags(), _memory, Proj, "notes", "feedback");
		var keys = feedback.Entries.Select(e => e.Key).ToList();
		keys.Should().BeEquivalentTo(["prefers-tabs"]);
	}

	[Fact]
	public async Task Search_Fts_FindsByToken_AndExcludesOthers()
	{
		var http = Http("memory:read,memory:write");
		var entries = McpInputs.Entries(new object[]
		{
			new { key = "auth-scopes", type = "project", description = "API key scopes", body = "scopes are enumerable, not wildcards" },
			new { key = "go-style", type = "reference", description = "Go conventions", body = "use tabs not spaces" },
		});
		await MemoryTools.UpsertAsync(http, Flags(), _memory, Proj, "notes", entries);

		var res = await MemoryTools.SearchAsync(http, Flags(), _memory, new PetBox.Tests.Memory.NoopUsageRecorder(), Proj, "notes", "scope");
		var keys = res.Entries.Select(e => e.Key).ToList();
		keys.Should().Contain("auth-scopes");      // "scope*" prefix-matches "scopes"
		keys.Should().NotContain("go-style");
	}

	[Fact]
	public async Task Upsert_AcceptsTypedEntries()
	{
		var http = Http("memory:read,memory:write");
		// typed-surface Phase 4: `entries` is now a typed MemoryEntryInputDto[] (the SDK emits a
		// rich input schema), so the old JSON-*string* fallback for stale-schema clients is gone —
		// a reconnect refreshes the cached schema (see McpToolInputs deviation note).
		var entries = McpInputs.EntriesJson("""[{"key":"k","type":"project","description":"d","body":"b"}]""");
		var res = await MemoryTools.UpsertAsync(http, Flags(), _memory, Proj, "strstore", entries);
		res.Added.Should().ContainSingle()
			.Which.Key.Should().Be("k");
	}

	[Fact]
	public async Task Upsert_DeletedTrue_SoftRemovesEntry()
	{
		var http = Http("memory:read,memory:write");
		var entries = McpInputs.Entries(new object[]
		{
			new { key = "temp", type = "project", description = "temp", body = "to be removed" },
		});
		await MemoryTools.UpsertAsync(http, Flags(), _memory, Proj, "notes", entries);

		var del = McpInputs.Entries(new object[] { new { key = "temp", deleted = true } });
		var res = await MemoryTools.UpsertAsync(http, Flags(), _memory, Proj, "notes", del);
		res.Removed.Should().Contain("temp");

		var list = await MemoryTools.ListAsync(http, Flags(), _memory, Proj, "notes");
		list.Entries.Select(e => e.Key)
			.Should().NotContain("temp");
	}

	// spec explicit-write-semantics: memory.upsert is a PATCH on edits (version > 0) — an
	// omitted field keeps its current value. Incident repro (yobapub, store=notes): a
	// tags-only edit used to wipe description AND body to empty; it must not.
	[Fact]
	public async Task Upsert_Patch_TagsOnlyEdit_KeepsDescriptionAndBody()
	{
		var http = Http("memory:read,memory:write");
		var created = (await MemoryTools.UpsertAsync(http, Flags(), _memory, Proj, "notes", McpInputs.Entries(new object[]
		{
			new { key = "k", type = "project", description = "keep-d", body = "keep-b", tags = "t1,t2" },
		}))).Added.Single();

		// The incident payload: only key/type/tags/version — description and body omitted.
		var res = await MemoryTools.UpsertAsync(http, Flags(), _memory, Proj, "notes", McpInputs.Entries(new object[]
		{
			new { key = "k", type = "project", tags = "t3", version = created.Version },
		}));
		res.Applied.Should().BeTrue();

		var after = (await MemoryTools.GetAsync(http, Flags(), _memory, new NoopUsageRecorder(), Proj, "notes", "k"))!;
		after.Description.Should().Be("keep-d");
		after.Body.Should().Be("keep-b");
		after.Tags.Should().Be("t3");
	}

	// spec explicit-write-semantics: an explicitly EMPTY field ("") is a deliberate clear —
	// distinct from an omitted (null) one, which stays unchanged.
	[Fact]
	public async Task Upsert_Patch_ExplicitEmptyClears_OmittedStays()
	{
		var http = Http("memory:read,memory:write");
		var created = (await MemoryTools.UpsertAsync(http, Flags(), _memory, Proj, "notes", McpInputs.Entries(new object[]
		{
			new { key = "c", type = "project", description = "d", body = "b", tags = "t" },
		}))).Added.Single();

		// body:"" clears the body; omitted description/tags stay.
		var res = await MemoryTools.UpsertAsync(http, Flags(), _memory, Proj, "notes", McpInputs.Entries(new object[]
		{
			new { key = "c", type = "project", body = "", version = created.Version },
		}));
		res.Applied.Should().BeTrue();

		var after = (await MemoryTools.GetAsync(http, Flags(), _memory, new NoopUsageRecorder(), Proj, "notes", "c"))!;
		after.Body.Should().BeEmpty();
		after.Description.Should().Be("d");
		after.Tags.Should().Be("t");
	}

	// CREATE path unchanged: a new entry (version 0) with partial fields starts the omitted
	// ones empty — PATCH merging only applies to edits.
	[Fact]
	public async Task Upsert_NewEntry_PartialFields_OmittedStartEmpty()
	{
		var http = Http("memory:read,memory:write");
		var res = await MemoryTools.UpsertAsync(http, Flags(), _memory, Proj, "notes", McpInputs.Entries(new object[]
		{
			new { key = "fresh", type = "project", body = "only-body" },
		}));
		res.Applied.Should().BeTrue();

		var after = (await MemoryTools.GetAsync(http, Flags(), _memory, new NoopUsageRecorder(), Proj, "notes", "fresh"))!;
		after.Body.Should().Be("only-body");
		after.Description.Should().BeEmpty();
		after.Tags.Should().BeEmpty();
	}

	// spec echo-compact-by-default (mirror of the tasks side): memory.upsert echoes
	// key/type/description/version but NOT the body unless bodyLen > 0. description (a one-liner)
	// stays to orient the merge; the heavy body is opt-in.
	[Fact]
	public async Task Upsert_EchoOmitsBodyByDefault_SlicesWithBodyLen()
	{
		var http = Http("memory:read,memory:write");
		var big = new string('y', 500);

		// Default echo: description present, body sliced to null.
		var entries = McpInputs.Entries(new object[]
		{
			new { key = "k", type = "project", description = "one-liner", body = big },
		});
		var added = (await MemoryTools.UpsertAsync(http, Flags(), _memory, Proj, "notes", entries))
			.Added.Single();
		added.Description.Should().Be("one-liner");
		added.Body.Should().BeNull();

		// bodyLen > 0: opt-in sliced body — first N chars + "…" when cut.
		var entries2 = McpInputs.Entries(new object[]
		{
			new { key = "k2", type = "project", description = "d", body = big },
		});
		var sliced = (await MemoryTools.UpsertAsync(http, Flags(), _memory, Proj, "notes", entries2, bodyLen: 300))
			.Added.Single(e => e.Key == "k2")
			.Body!;
		sliced.Length.Should().Be(301);
		sliced.Should().EndWith("…");
	}

	// spec read-snippet-on-demand + bounded-result-sets: list/search cap at `limit` and snippet
	// bodies at `bodyLen` (full by default), so a read can't dump an unbounded wall of bodies.
	[Fact]
	public async Task ListAndSearch_RespectLimit_AndBodyLen()
	{
		var http = Http("memory:read,memory:write");
		var big = new string('x', 300);
		var entries = McpInputs.Entries(new object[]
		{
			new { key = "a", type = "project", description = "da", body = "alpha " + big },
			new { key = "b", type = "project", description = "db", body = "alpha short" },
			new { key = "c", type = "project", description = "dc", body = "alpha scope" },
		});
		await MemoryTools.UpsertAsync(http, Flags(), _memory, Proj, "notes", entries);

		// list: limit caps the count.
		(await MemoryTools.ListAsync(http, Flags(), _memory, Proj, "notes", limit: 2))
			.Entries.Count.Should().Be(2);

		// list: bodyLen snippets the body of entry 'a' (the long one).
		var aBody = (await MemoryTools.ListAsync(http, Flags(), _memory, Proj, "notes", bodyLen: 100))
			.Entries.Single(e => e.Key == "a")
			.Body!;
		aBody.Length.Should().Be(101);
		aBody.Should().EndWith("…");

		// search: the same limit bounds an FTS sweep ("alpha" hits all three).
		(await MemoryTools.SearchAsync(http, Flags(), _memory, new PetBox.Tests.Memory.NoopUsageRecorder(), Proj, "notes", "alpha", limit: 2))
			.Entries.Count.Should().Be(2);
	}

	static IHttpContextAccessor Http(string scopes)
	{
		var id = new ClaimsIdentity([new Claim("project", Proj), new Claim("scopes", scopes)], "test");
		return new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(id) } };
	}

	static FeatureFlags Flags()
	{
		var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
		{
			["Features:Tasks"] = "true",
			["Features:Memory"] = "true",
		}).Build();
		return new FeatureFlags(cfg);
	}
}
