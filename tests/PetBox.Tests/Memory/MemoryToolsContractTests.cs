using System.Security.Claims;
using LinqToDB;
using Microsoft.AspNetCore.Http;
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
		TestDirs.CleanupOrDefer(_dir);
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
		await Assert.ThrowsAsync<ArgumentException>(() => MemoryTools.UpsertAsync(http, Flags(), _db, _memory, Proj, "notes", entries));
	}

	// A missing key is a not-found ERROR, never a null result: memory_get declares an
	// outputSchema, so a null (no structured content) is rejected by strict MCP clients as
	// -32600. The throw rides the isError channel via McpErrorEnvelopeFilter — which strict
	// clients accept (bug mcp-nullable-get-strict-32600). InvalidOperationException matches the
	// surface-wide not-found convention.
	[Fact]
	public async Task Get_MissingKey_Throws()
	{
		var http = Http("memory:read,memory:write");
		// Populate the store first so this isolates key-not-found (the former null-return path)
		// from store-not-found.
		await MemoryTools.UpsertAsync(http, Flags(), _db, _memory, Proj, "notes", McpInputs.Entries(new object[]
		{
			new { key = "present", type = "project", description = "d", body = "b" },
		}));
		await Assert.ThrowsAsync<InvalidOperationException>(
			() => MemoryTools.GetAsync(http, Flags(), _db, _memory, new NoopUsageRecorder(), Proj, "notes", "no-such-key"));
	}

	// spec addressed-read-batched: `keys` reads N entries in ONE call, rows in the asked order
	// (bodies full, like the single get).
	[Fact]
	public async Task Get_Batch_ReturnsEveryAddressedEntry()
	{
		var http = Http("memory:read,memory:write");
		await MemoryTools.UpsertAsync(http, Flags(), _db, _memory, Proj, "notes", McpInputs.Entries(new object[]
		{
			new { key = "a", type = "project", description = "da", body = "ba" },
			new { key = "b", type = "project", description = "db", body = "bb" },
			new { key = "c", type = "project", description = "dc", body = "bc" },
		}));

		var got = await MemoryTools.GetAsync(http, Flags(), _db, _memory, new NoopUsageRecorder(), Proj, "notes",
			keys: ["c", "a"]);

		got.Entries.Select(e => e.Key).Should().Equal("c", "a");
		got.Entries.Select(e => e.Body).Should().Equal("bc", "ba");
	}

	// The batch is a SOFT filter (the tasks_search `keys[]` contract): a key that resolves to
	// nothing is silently dropped, and an all-missing batch is an EMPTY result, not an error —
	// only a lone `key` miss stays a not-found throw (Get_MissingKey_Throws).
	[Fact]
	public async Task Get_Batch_MissingKey_IsDroppedNotAnError()
	{
		var http = Http("memory:read,memory:write");
		await MemoryTools.UpsertAsync(http, Flags(), _db, _memory, Proj, "notes", McpInputs.Entries(new object[]
		{
			new { key = "present", type = "project", description = "d", body = "b" },
		}));

		var got = await MemoryTools.GetAsync(http, Flags(), _db, _memory, new NoopUsageRecorder(), Proj, "notes",
			keys: ["present", "no-such-key"]);
		got.Entries.Select(e => e.Key).Should().Equal("present");

		var none = await MemoryTools.GetAsync(http, Flags(), _db, _memory, new NoopUsageRecorder(), Proj, "notes",
			keys: ["no-such-key", "nor-this"]);
		none.Entries.Should().BeEmpty();
	}

	[Fact]
	public async Task Upsert_AutoVivifies_NormalisesTags_AndFiltersByType()
	{
		var http = Http("memory:read,memory:write");

		// Cold upsert (no store_create) auto-creates the store; tags get normalised.
		var entries = McpInputs.Entries(new object[]
		{
			new { key = "go-style", type = "reference", description = "Go", body = "tabs", tags = new[] { "Go", " STYLE ", "go" } },
			new { key = "prefers-tabs", type = "feedback", description = "tabs", body = "user likes tabs" },
		});
		await MemoryTools.UpsertAsync(http, Flags(), _db, _memory, Proj, "notes", entries);
		(await _store.ExistsAsync(Proj, "notes")).Should().BeTrue();

		// Tags normalised: lowercased, trimmed, de-duped.
		var all = await MemoryTools.SearchAsync(http, Flags(), _db, _memory, new PetBox.Tests.Memory.NoopUsageRecorder(),
			scope: "project", store: "notes");
		var go = all.Items.Single(e => e.Key == "go-style");
		go.Tags.Should().Equal("go", "style");
		go.Type.Should().Be("Reference");

		// Type filter narrows the listing.
		var feedback = await MemoryTools.SearchAsync(http, Flags(), _db, _memory, new PetBox.Tests.Memory.NoopUsageRecorder(),
			scope: "project", store: "notes", type: "feedback");
		var keys = feedback.Items.Select(e => e.Key).ToList();
		keys.Should().BeEquivalentTo(["prefers-tabs"]);
	}

	// memory_search with q: the single-store FTS scenario (scope:"project" + store) —
	// hybrid retriever underneath, lexical tokens prefix-matched.
	[Fact]
	public async Task Search_Fts_FindsByToken_AndExcludesOthers()
	{
		var http = Http("memory:read,memory:write");
		var entries = McpInputs.Entries(new object[]
		{
			new { key = "auth-scopes", type = "project", description = "API key scopes", body = "scopes are enumerable, not wildcards" },
			new { key = "go-style", type = "reference", description = "Go conventions", body = "use tabs not spaces" },
		});
		await MemoryTools.UpsertAsync(http, Flags(), _db, _memory, Proj, "notes", entries);

		var res = await MemoryTools.SearchAsync(http, Flags(), _db, _memory, new PetBox.Tests.Memory.NoopUsageRecorder(),
			"scope", scope: "project", store: "notes");
		var keys = res.Items.Select(e => e.Key).ToList();
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
		var res = await MemoryTools.UpsertAsync(http, Flags(), _db, _memory, Proj, "strstore", entries);
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
		await MemoryTools.UpsertAsync(http, Flags(), _db, _memory, Proj, "notes", entries);

		var del = McpInputs.Entries(new object[] { new { key = "temp", deleted = true } });
		var res = await MemoryTools.UpsertAsync(http, Flags(), _db, _memory, Proj, "notes", del);
		res.Removed.Should().Contain("temp");

		var list = await MemoryTools.SearchAsync(http, Flags(), _db, _memory, new PetBox.Tests.Memory.NoopUsageRecorder(),
			scope: "project", store: "notes");
		list.Items.Select(e => e.Key)
			.Should().NotContain("temp");
	}

	// spec explicit-write-semantics: memory_upsert is a PATCH on edits (version > 0) — an
	// omitted field keeps its current value. Incident repro (yobapub, store=notes): a
	// tags-only edit used to wipe description AND body to empty; it must not.
	[Fact]
	public async Task Upsert_Patch_TagsOnlyEdit_KeepsDescriptionAndBody()
	{
		var http = Http("memory:read,memory:write");
		var created = (await MemoryTools.UpsertAsync(http, Flags(), _db, _memory, Proj, "notes", McpInputs.Entries(new object[]
		{
			new { key = "k", type = "project", description = "keep-d", body = "keep-b", tags = new[] { "t1", "t2" } },
		}))).Added.Single();

		// The incident payload: only key/type/tags/version — description and body omitted.
		var res = await MemoryTools.UpsertAsync(http, Flags(), _db, _memory, Proj, "notes", McpInputs.Entries(new object[]
		{
			new { key = "k", type = "project", tags = new[] { "t3" }, version = created.Version },
		}));
		res.Applied.Should().BeTrue();

		var after = (await MemoryTools.GetAsync(http, Flags(), _db, _memory, new NoopUsageRecorder(), Proj, "notes", "k")).Entries.Single();
		after.Description.Should().Be("keep-d");
		after.Body.Should().Be("keep-b");
		after.Tags.Should().Equal("t3");
	}

	// spec explicit-write-semantics: an explicitly EMPTY field ("") is a deliberate clear —
	// distinct from an omitted (null) one, which stays unchanged.
	[Fact]
	public async Task Upsert_Patch_ExplicitEmptyClears_OmittedStays()
	{
		var http = Http("memory:read,memory:write");
		var created = (await MemoryTools.UpsertAsync(http, Flags(), _db, _memory, Proj, "notes", McpInputs.Entries(new object[]
		{
			new { key = "c", type = "project", description = "d", body = "b", tags = new[] { "t" } },
		}))).Added.Single();

		// body:"" clears the body; omitted description/tags stay.
		var res = await MemoryTools.UpsertAsync(http, Flags(), _db, _memory, Proj, "notes", McpInputs.Entries(new object[]
		{
			new { key = "c", type = "project", body = "", version = created.Version },
		}));
		res.Applied.Should().BeTrue();

		var after = (await MemoryTools.GetAsync(http, Flags(), _db, _memory, new NoopUsageRecorder(), Proj, "notes", "c")).Entries.Single();
		after.Body.Should().BeEmpty();
		after.Description.Should().Be("d");
		after.Tags.Should().Equal("t");
	}

	// CREATE path unchanged: a new entry (version 0) with partial fields starts the omitted
	// ones empty — PATCH merging only applies to edits.
	[Fact]
	public async Task Upsert_NewEntry_PartialFields_OmittedStartEmpty()
	{
		var http = Http("memory:read,memory:write");
		var res = await MemoryTools.UpsertAsync(http, Flags(), _db, _memory, Proj, "notes", McpInputs.Entries(new object[]
		{
			new { key = "fresh", type = "project", body = "only-body" },
		}));
		res.Applied.Should().BeTrue();

		var after = (await MemoryTools.GetAsync(http, Flags(), _db, _memory, new NoopUsageRecorder(), Proj, "notes", "fresh")).Entries.Single();
		after.Body.Should().Be("only-body");
		after.Description.Should().BeEmpty();
		after.Tags.Should().BeEmpty();
	}

	// spec echo-compact-by-default (mirror of the tasks side): memory_upsert echoes
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
		var added = (await MemoryTools.UpsertAsync(http, Flags(), _db, _memory, Proj, "notes", entries))
			.Added.Single();
		added.Description.Should().Be("one-liner");
		added.Body.Should().BeNull();

		// bodyLen > 0: opt-in sliced body — first N chars + "…" when cut.
		var entries2 = McpInputs.Entries(new object[]
		{
			new { key = "k2", type = "project", description = "d", body = big },
		});
		var sliced = (await MemoryTools.UpsertAsync(http, Flags(), _db, _memory, Proj, "notes", entries2, bodyLen: 300))
			.Added.Single(e => e.Key == "k2")
			.Body!;
		sliced.Length.Should().Be(301);
		sliced.Should().EndWith("…");
	}

	// spec bodylen-uniform-contract + bounded-result-sets: both modes cap at `limit` and follow
	// the uniform bodyLen knob (omitted = a ~240-char snippet — the compact listing default;
	// -1 = full; 0 = none; N>0 = an N-char snippet), so a read can't dump an unbounded wall.
	[Fact]
	public async Task Search_BothModes_RespectLimit_AndBodyLen()
	{
		var http = Http("memory:read,memory:write");
		var big = new string('x', 300);
		var entries = McpInputs.Entries(new object[]
		{
			new { key = "a", type = "project", description = "da", body = "alpha " + big },
			new { key = "b", type = "project", description = "db", body = "alpha short" },
			new { key = "c", type = "project", description = "dc", body = "alpha scope" },
		});
		await MemoryTools.UpsertAsync(http, Flags(), _db, _memory, Proj, "notes", entries);

		// listing: limit caps the count.
		(await MemoryTools.SearchAsync(http, Flags(), _db, _memory, new PetBox.Tests.Memory.NoopUsageRecorder(),
			scope: "project", store: "notes", limit: 2))
			.Items.Count.Should().Be(2);

		// listing: bodyLen:100 snippets the body of entry 'a' (the long one, 306 chars).
		var aBody = (await MemoryTools.SearchAsync(http, Flags(), _db, _memory, new PetBox.Tests.Memory.NoopUsageRecorder(),
			scope: "project", store: "notes", bodyLen: 100))
			.Items.Single(e => e.Key == "a")
			.Body!;
		aBody.Length.Should().Be(101);
		aBody.Should().EndWith("…");

		// listing default (omitted): a ~240-char snippet of the long body.
		var aDefault = (await MemoryTools.SearchAsync(http, Flags(), _db, _memory, new PetBox.Tests.Memory.NoopUsageRecorder(),
			scope: "project", store: "notes")).Items.Single(e => e.Key == "a").Body!;
		aDefault.Length.Should().Be(241);
		aDefault.Should().EndWith("…");

		// -1: the full body; 0: no body (omitted → null).
		(await MemoryTools.SearchAsync(http, Flags(), _db, _memory, new PetBox.Tests.Memory.NoopUsageRecorder(),
			scope: "project", store: "notes", bodyLen: -1)).Items.Single(e => e.Key == "a").Body!.Length.Should().Be(306);
		(await MemoryTools.SearchAsync(http, Flags(), _db, _memory, new PetBox.Tests.Memory.NoopUsageRecorder(),
			scope: "project", store: "notes", bodyLen: 0)).Items.Single(e => e.Key == "a").Body.Should().BeNull();

		// with q: the same limit bounds an FTS sweep ("alpha" hits all three).
		(await MemoryTools.SearchAsync(http, Flags(), _db, _memory, new PetBox.Tests.Memory.NoopUsageRecorder(),
			"alpha", scope: "project", store: "notes", limit: 2))
			.Items.Count.Should().Be(2);
	}

	// WATERMARK over the MCP surface: the store `currentVersion` echoed by one upsert is a valid
	// baseline for the next — even above the edited entry's own version (a second entry advanced
	// the cursor). A baseline above the store cursor is a FutureBaseline conflict carrying Reason.
	[Fact]
	public async Task Upsert_EchoCurrentVersion_IsValidNextBaseline_FutureRejected()
	{
		var http = Http("memory:read,memory:write");
		await MemoryTools.UpsertAsync(http, Flags(), _db, _memory, Proj, "notes", McpInputs.Entries(new object[]
		{
			new { key = "a", type = "project", description = "d", body = "b" },
		}));                                                                            // v1
		var second = await MemoryTools.UpsertAsync(http, Flags(), _db, _memory, Proj, "notes", McpInputs.Entries(new object[]
		{
			new { key = "c", type = "project", description = "d", body = "b" },
		}));                                                                            // v2 -> store cursor
		var cursor = second.CurrentVersion;

		// Edit 'a' (own version 1) at the store cursor baseline — accepted.
		var edit = await MemoryTools.UpsertAsync(http, Flags(), _db, _memory, Proj, "notes", McpInputs.Entries(new object[]
		{
			new { key = "a", type = "project", body = "edited", version = cursor },
		}));
		edit.Applied.Should().BeTrue();
		edit.Conflicts.Should().BeEmpty();

		// A baseline above the store cursor is a FutureBaseline conflict, Reason surfaced.
		var future = await MemoryTools.UpsertAsync(http, Flags(), _db, _memory, Proj, "notes", McpInputs.Entries(new object[]
		{
			new { key = "a", type = "project", body = "x", version = cursor + 500 },
		}));
		future.Applied.Should().BeFalse();
		var conflict = future.Conflicts.Single();
		conflict.Kind.Should().Be("FutureBaseline");
		conflict.Reason.Should().Contain("another board/scope");

		// spec upsert-ack-echo-clean: a write that did NOT apply echoes NOTHING — the conflict is
		// the whole story, and added/updated/removed stay empty (they used to carry 'a's current
		// state and read as if the write landed).
		future.Added.Should().BeEmpty();
		future.Updated.Should().BeEmpty();
		future.Removed.Should().BeEmpty();
	}

	static IHttpContextAccessor Http(string scopes)
	{
		var id = new ClaimsIdentity([new Claim("project", Proj), new Claim("scopes", scopes)], "test");
		return new HttpContextAccessor { HttpContext = new DefaultHttpContext { RequestServices = TestProjectCatalog.Services, User = new ClaimsPrincipal(id) } };
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
