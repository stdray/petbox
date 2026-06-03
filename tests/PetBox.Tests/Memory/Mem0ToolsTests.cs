using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
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

// mem0-compatible MCP surface (Mem0Tools): scope mapping (user_id->store, agent/run
// ->tags), reversible id codec, verbatim add (infer deferred), update/delete version
// flow, scoped delete_all, and structured errors. Mirrors the MemoryToolsContractTests
// harness (temp dirs, real MemoryService, claims-based auth, camelCase result JSON).
[Collection("DataModule")]
public sealed class Mem0ToolsTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<MemoryDb> _factory;
	readonly MemoryStore _store;
	readonly MemoryService _memory;

	public Mem0ToolsTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-mem0-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		MigrationRunner.Run(cs);
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
	public async Task Add_Then_Search_Roundtrip_SanitizesUserIdStore()
	{
		var http = Http("memory:read,memory:write");
		var add = Json(await Mem0Tools.AddMemoryAsync(http, Flags(), _memory, Proj,
			messages: Msg("auth uses jwt middleware"), user_id: "Alice@X"));
		var first = add.GetProperty("results")[0];
		first.GetProperty("event").GetString().Should().Be("ADD");
		var id = first.GetProperty("id").GetString()!;

		// user_id "Alice@X" is not a valid store name → sanitized + hashed; id embeds it.
		var store = Mem0Map.StoreFromUserId("Alice@X");
		id.Should().StartWith(store + "__m-");

		var search = Json(await Mem0Tools.SearchMemoriesAsync(http, Flags(), _memory, Proj, "jwt", user_id: "Alice@X"));
		var hits = search.GetProperty("results").EnumerateArray().ToList();
		hits.Should().ContainSingle();
		hits[0].GetProperty("id").GetString().Should().Be(id);
		hits[0].GetProperty("memory").GetString().Should().Be("auth uses jwt middleware");
		hits[0].GetProperty("user_id").GetString().Should().Be(store);
	}

	[Fact]
	public async Task Add_DefaultStore_WhenNoUserId()
	{
		var http = Http("memory:read,memory:write");
		var add = Json(await Mem0Tools.AddMemoryAsync(http, Flags(), _memory, Proj, messages: Msg("a default-store note")));
		add.GetProperty("results")[0].GetProperty("id").GetString().Should().StartWith("default__");

		var list = Json(await Mem0Tools.GetMemoriesAsync(http, Flags(), _memory, Proj));
		list.GetProperty("results").EnumerateArray().Should().ContainSingle()
			.Which.GetProperty("memory").GetString().Should().Be("a default-store note");
	}

	[Fact]
	public async Task Update_PreservesMetadata_Then_MetadataOnlyUpdate_Applies()
	{
		var http = Http("memory:read,memory:write");
		var add = Json(await Mem0Tools.AddMemoryAsync(http, Flags(), _memory, Proj,
			messages: Msg("original text"), user_id: "u1", metadata: Meta(new { topic = "auth" })));
		var id = add.GetProperty("results")[0].GetProperty("id").GetString()!;

		// Text update preserves metadata.
		Json(await Mem0Tools.UpdateMemoryAsync(http, Flags(), _memory, Proj, id, text: "updated text"))
			.GetProperty("event").GetString().Should().Be("UPDATE");
		var got = Json(await Mem0Tools.GetMemoryAsync(http, Flags(), _memory, Proj, id));
		got.GetProperty("memory").GetString().Should().Be("updated text");
		got.GetProperty("metadata").GetProperty("topic").GetString().Should().Be("auth");

		// Metadata-only update must apply (proves SamePayload compares Metadata — body unchanged).
		Json(await Mem0Tools.UpdateMemoryAsync(http, Flags(), _memory, Proj, id, metadata: Meta(new { topic = "security" })))
			.GetProperty("event").GetString().Should().Be("UPDATE");
		var got2 = Json(await Mem0Tools.GetMemoryAsync(http, Flags(), _memory, Proj, id));
		got2.GetProperty("memory").GetString().Should().Be("updated text");
		got2.GetProperty("metadata").GetProperty("topic").GetString().Should().Be("security");
	}

	[Fact]
	public async Task Delete_Then_Get_NotFound_AndIdempotent()
	{
		var http = Http("memory:read,memory:write");
		var add = Json(await Mem0Tools.AddMemoryAsync(http, Flags(), _memory, Proj, messages: Msg("ephemeral"), user_id: "u1"));
		var id = add.GetProperty("results")[0].GetProperty("id").GetString()!;

		Json(await Mem0Tools.DeleteMemoryAsync(http, Flags(), _memory, Proj, id))
			.GetProperty("event").GetString().Should().Be("DELETE");
		Json(await Mem0Tools.GetMemoryAsync(http, Flags(), _memory, Proj, id))
			.GetProperty("error").GetProperty("type").GetString().Should().Be("NotFound");
		// Idempotent: deleting again still succeeds.
		Json(await Mem0Tools.DeleteMemoryAsync(http, Flags(), _memory, Proj, id))
			.GetProperty("event").GetString().Should().Be("DELETE");
	}

	[Fact]
	public async Task DeleteAll_Scoped_ByAgentTag()
	{
		var http = Http("memory:read,memory:write");
		await Mem0Tools.AddMemoryAsync(http, Flags(), _memory, Proj, messages: Msg("note A"), user_id: "team", agent_id: "alpha");
		await Mem0Tools.AddMemoryAsync(http, Flags(), _memory, Proj, messages: Msg("note B"), user_id: "team", agent_id: "beta");

		var res = Json(await Mem0Tools.DeleteAllMemoriesAsync(http, Flags(), _memory, Proj, user_id: "team", agent_id: "alpha"));
		res.GetProperty("deleted_count").GetInt32().Should().Be(1);

		var remaining = Json(await Mem0Tools.GetMemoriesAsync(http, Flags(), _memory, Proj, user_id: "team"));
		remaining.GetProperty("results").EnumerateArray().Should().ContainSingle()
			.Which.GetProperty("memory").GetString().Should().Be("note B");
	}

	[Fact]
	public async Task MalformedId_ReturnsStructuredError()
	{
		var http = Http("memory:read,memory:write");
		Json(await Mem0Tools.GetMemoryAsync(http, Flags(), _memory, Proj, "not-an-id"))
			.GetProperty("error").GetProperty("type").GetString().Should().Be("ArgumentException");
	}

	[Fact]
	public async Task Write_WithoutWriteScope_ReturnsUnauthorizedError()
	{
		var http = Http("memory:read"); // read-only key
		Json(await Mem0Tools.AddMemoryAsync(http, Flags(), _memory, Proj, messages: Msg("x"), user_id: "u1"))
			.GetProperty("error").GetProperty("type").GetString().Should().Be("UnauthorizedAccessException");
	}

	[Fact]
	public async Task Search_UnknownUser_ReturnsEmpty()
	{
		var http = Http("memory:read,memory:write");
		Json(await Mem0Tools.SearchMemoriesAsync(http, Flags(), _memory, Proj, "anything", user_id: "nobody"))
			.GetProperty("results").EnumerateArray().Should().BeEmpty();
	}

	[Fact]
	public async Task ProjectKey_Omitted_DefaultsToKeyClaim()
	{
		var http = Http("memory:read,memory:write"); // claim project = Proj
		var add = Json(await Mem0Tools.AddMemoryAsync(http, Flags(), _memory, messages: Msg("from claim project"), user_id: "u1"));
		add.GetProperty("results")[0].GetProperty("id").GetString().Should().StartWith("u1__");
		Json(await Mem0Tools.SearchMemoriesAsync(http, Flags(), _memory, query: "claim", user_id: "u1"))
			.GetProperty("results").EnumerateArray().Should().ContainSingle();
	}

	[Fact]
	public async Task ProjectKey_Omitted_WithCrossProjectKey_Errors()
	{
		var http = HttpFor("*", "memory:read,memory:write");
		Json(await Mem0Tools.AddMemoryAsync(http, Flags(), _memory, messages: Msg("x"), user_id: "u1"))
			.GetProperty("error").GetProperty("type").GetString().Should().Be("ArgumentException");
	}

	[Fact]
	public async Task Search_Filters_NarrowByMetadata()
	{
		var http = Http("memory:read,memory:write");
		await Mem0Tools.AddMemoryAsync(http, Flags(), _memory, Proj, messages: Msg("login uses jwt"), user_id: "u1", metadata: Meta(new { area = "auth" }));
		await Mem0Tools.AddMemoryAsync(http, Flags(), _memory, Proj, messages: Msg("ui shows jwt token"), user_id: "u1", metadata: Meta(new { area = "ui" }));

		// both bodies match the query "jwt"; the metadata filter narrows to area=auth.
		var res = Json(await Mem0Tools.SearchMemoriesAsync(http, Flags(), _memory, Proj, "jwt", user_id: "u1", filters: Meta(new { area = "auth" })));
		var hits = res.GetProperty("results").EnumerateArray().ToList();
		hits.Should().ContainSingle();
		hits[0].GetProperty("memory").GetString().Should().Be("login uses jwt");
	}

	[Fact]
	public async Task Metadata_And_Filters_Unwrap_StringEncodedJson()
	{
		// Some MCP clients (e.g. Claude Code) double-encode object args as a JSON *string*.
		var http = Http("memory:read,memory:write");
		var metaAsString = JsonSerializer.SerializeToElement("{\"area\":\"auth\"}"); // ValueKind == String
		await Mem0Tools.AddMemoryAsync(http, Flags(), _memory, Proj, messages: Msg("login jwt"), user_id: "u1", metadata: metaAsString);
		await Mem0Tools.AddMemoryAsync(http, Flags(), _memory, Proj, messages: Msg("ui jwt token"), user_id: "u1", metadata: Meta(new { area = "ui" }));

		// filters also string-encoded → must still narrow (not silently match-all).
		var filtersAsString = JsonSerializer.SerializeToElement("{\"area\":\"auth\"}");
		var res = Json(await Mem0Tools.SearchMemoriesAsync(http, Flags(), _memory, Proj, "jwt", user_id: "u1", filters: filtersAsString));
		var hits = res.GetProperty("results").EnumerateArray().ToList();
		hits.Should().ContainSingle();
		hits[0].GetProperty("memory").GetString().Should().Be("login jwt");
		// metadata round-trips as an OBJECT (GetProperty would throw if stored as a string).
		hits[0].GetProperty("metadata").GetProperty("area").GetString().Should().Be("auth");
	}

	static IHttpContextAccessor Http(string scopes) => HttpFor(Proj, scopes);

	static IHttpContextAccessor HttpFor(string project, string scopes)
	{
		var id = new ClaimsIdentity([new Claim("project", project), new Claim("scopes", scopes)], "test");
		return new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(id) } };
	}

	static FeatureFlags Flags()
	{
		var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
		{
			["Features:Memory"] = "true",
		}).Build();
		return new FeatureFlags(cfg);
	}

	static JsonElement Msg(string s) => JsonSerializer.SerializeToElement(s);
	static JsonElement Meta(object o) => JsonSerializer.SerializeToElement(o);

	static readonly JsonSerializerOptions CamelCase = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
	static JsonElement Json(object? o) => JsonSerializer.SerializeToElement(o, CamelCase);
}

// Pure unit coverage for the mem0 scope/id mapping (no DB needed).
public sealed class Mem0MapTests
{
	[Theory]
	[InlineData("Alice@X")]
	[InlineData("user.name+tag@example.com")]
	[InlineData("")]
	[InlineData("   ")]
	[InlineData("UPPER")]
	[InlineData("simple")]
	[InlineData("clean-ok_1")]
	public void StoreFromUserId_IsDeterministic_AndStoreNameValid(string userId)
	{
		var a = Mem0Map.StoreFromUserId(userId);
		var b = Mem0Map.StoreFromUserId(userId);
		a.Should().Be(b);
		Regex.IsMatch(a, "^[a-z][a-z0-9_-]{0,99}$").Should().BeTrue($"'{a}' must be a valid store name");
	}

	[Fact]
	public void StoreFromUserId_PassesCleanIds_DefaultsBlank_DistinguishesInputs()
	{
		Mem0Map.StoreFromUserId(null).Should().Be("default");
		Mem0Map.StoreFromUserId("").Should().Be("default");
		Mem0Map.StoreFromUserId("simple").Should().Be("simple");          // already valid → passthrough
		Mem0Map.StoreFromUserId("clean-ok_1").Should().Be("clean-ok_1");
		Mem0Map.StoreFromUserId("Alice@X").Should().NotBe(Mem0Map.StoreFromUserId("Alice@Y")); // hash distinguishes
	}

	[Fact]
	public void IdCodec_Roundtrips()
	{
		var id = Mem0Map.MakeId("alice-1a2b", "m-deadbeef");
		Mem0Map.TryDecodeId(id, out var store, out var key).Should().BeTrue();
		store.Should().Be("alice-1a2b");
		key.Should().Be("m-deadbeef");
		Mem0Map.TryDecodeId("no-separator", out _, out _).Should().BeFalse();
	}
}
