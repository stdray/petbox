using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using PetBox.Config;
using PetBox.Config.Data;
using PetBox.Core.Data;
using PetBox.Core.Settings;
using PetBox.Web.Mcp;
using PetBox.Web.Mcp.Contract;

namespace PetBox.Tests.Mcp;

// The config_binding family on the uniform-entity-verbs matrix (config_binding_upsert / _search /
// _delta / _get), exercised through the MCP adapter over a real per-workspace ConfigDb. Config is
// NOT temporally watermarked — a binding is PUT by (path, tag SET), immutable, keyed by an
// auto-increment id — so this verifies the documented deviations: PUT-supersede, added/updated
// classification, the max-id cursor for delta, the lexical-substring q, and secret-safety.
public sealed class ConfigBindingUniformVerbsTests : IDisposable
{
	const string Ws = "w";
	readonly string _dir;
	readonly ScopedDbFactory<ConfigDb> _inner;
	readonly ConfigDbFactory _factory;
	readonly ISecretEncryptor _secrets;

	public ConfigBindingUniformVerbsTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-config-verbs-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		_inner = new ScopedDbFactory<ConfigDb>(Path.Combine(_dir, "config"), Scope.Workspace,
			c => new ConfigDb(ConfigDb.CreateOptions(c)), ConfigSchema.Ensure);
		_factory = new ConfigDbFactory(_inner);
		_secrets = new AesGcmSecretEncryptor(Options.Create(new SecretEncryptorOptions { MasterKey = "test-master-key" }));
	}

	public void Dispose()
	{
		_inner.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	static IHttpContextAccessor Http() =>
		new HttpContextAccessor
		{
			HttpContext = new DefaultHttpContext
			{
				User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("scopes", "admin:provision")], "test")),
			},
		};

	static ConfigBindingItemInput Item(string path, string value, string? kind = null) =>
		new() { Path = path, Tags = $"ws:{Ws}", Value = value, Kind = kind };

	Task<ConfigBindingsUpsertResult> Upsert(IHttpContextAccessor http, params ConfigBindingItemInput[] items) =>
		ConfigTools.BindingUpsertAsync(http, _factory, _secrets, Ws, items);

	[Fact]
	public async Task Upsert_Batch_Creates_Then_PutSupersedes()
	{
		var http = Http();
		var p1 = $"a/{Guid.NewGuid():N}"[..12];
		var p2 = $"b/{Guid.NewGuid():N}"[..12];

		var created = await Upsert(http, Item(p1, "1"), Item(p2, "2"));
		created.Applied.Should().BeTrue();
		created.Added.Should().HaveCount(2);
		created.Updated.Should().BeEmpty();
		created.Superseded.Should().BeEmpty();
		created.Conflicts.Should().BeEmpty();               // config PUT has no CAS conflict
		created.CurrentVersion.Should().BeGreaterThan(0);   // max-id cursor

		// PUT the same (path, tagset) again → supersedes the twin, reported in updated + superseded.
		var replaced = await Upsert(http, Item(p1, "1b"));
		replaced.Applied.Should().BeTrue();
		replaced.Added.Should().BeEmpty();
		replaced.Updated.Should().ContainSingle(r => r.Path == p1);
		replaced.Superseded.Should().ContainSingle();
		replaced.CurrentVersion.Should().BeGreaterThan(created.CurrentVersion); // a new id minted
	}

	[Fact]
	public async Task Search_List_Then_LexicalQuery_And_PathPrefix()
	{
		var http = Http();
		await Upsert(http, Item("svc/url", "https://x"), Item("svc/key", "plainkey"), Item("db/dsn", "postgres://y"));

		// List (no q): deterministic, path-ordered, no retrievers.
		var list = await ConfigTools.BindingSearchAsync(http, _factory, Ws);
		list.Retrievers.Should().BeNull();
		list.Bindings.Select(b => b.Path).Should().Contain(["db/dsn", "svc/key", "svc/url"]);

		// pathPrefix narrows to a subtree.
		var svc = await ConfigTools.BindingSearchAsync(http, _factory, Ws, pathPrefix: "svc/");
		svc.Bindings.Should().OnlyContain(b => b.Path.StartsWith("svc/"));

		// q: a case-insensitive substring over path/tags/plaintext-value; degrades to the lexical floor.
		var q = await ConfigTools.BindingSearchAsync(http, _factory, Ws, q: "postgres");
		q.Bindings.Should().ContainSingle(b => b.Path == "db/dsn");
		q.Retrievers.Should().NotBeNull();
		q.Retrievers!.Lexical.Should().BeTrue();
		q.Retrievers.Semantic.Should().BeFalse();
		q.Retrievers.Degraded.Should().BeFalse();
	}

	[Fact]
	public async Task Delta_ReturnsBindingsSinceIdCursor()
	{
		var http = Http();
		var first = await Upsert(http, Item($"d/{Guid.NewGuid():N}"[..10], "1"));
		var cursor = first.CurrentVersion;

		await Upsert(http, Item($"d/{Guid.NewGuid():N}"[..10], "2"));
		await Upsert(http, Item($"d/{Guid.NewGuid():N}"[..10], "3"));

		var delta = await ConfigTools.BindingDeltaAsync(http, _factory, Ws, cursor);
		delta.Added.Should().HaveCount(2);                          // only the post-cursor ids
		delta.Added.Should().OnlyContain(r => true);
		delta.Updated.Should().BeEmpty();                           // immutable rows → adds only
		delta.CurrentVersion.Should().BeGreaterThan(cursor);
	}

	[Fact]
	public async Task Get_ById_And_Missing_IsError()
	{
		var http = Http();
		var id = (await Upsert(http, Item("get/me", "v"))).Added.Single().Id;

		var got = await ConfigTools.BindingGetAsync(http, _factory, Ws, id);
		got.Id.Should().Be(id);
		got.Path.Should().Be("get/me");

		var act = () => ConfigTools.BindingGetAsync(http, _factory, Ws, 999_999);
		await act.Should().ThrowAsync<InvalidOperationException>();
	}

	[Fact]
	public async Task Secret_StoredEncrypted_NeverReturnedAsValue()
	{
		var http = Http();
		var id = (await Upsert(http, Item("sec/key", "topsecret", kind: "Secret"))).Added.Single().Id;

		// The row is stored encrypted (empty Value, ciphertext set).
		var row = _factory.GetConfigDb(Ws).Bindings.First(b => b.Id == id);
		row.Value.Should().BeEmpty();
		row.Ciphertext.Should().NotBeNullOrEmpty();

		// get/search carry id/path/tags/kind only — never a value field, so the plaintext can't leak.
		var got = await ConfigTools.BindingGetAsync(http, _factory, Ws, id);
		got.Kind.Should().Be("Secret");
		var search = await ConfigTools.BindingSearchAsync(http, _factory, Ws, q: "topsecret");
		search.Bindings.Should().BeEmpty(); // a Secret's value is not matchable (stored encrypted, empty plaintext)
	}
}
