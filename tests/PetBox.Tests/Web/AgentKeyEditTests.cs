using System.Net;
using LinqToDB;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Auth;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Tests.Web;

// admin-ui-apikey-edit-lastused (spec apikey-mutable + apikey-last-used).
//
// The admin key pages gain an EDITOR (rename / re-scope / set+clear defaultProject) and a
// "last used" column. Both land on the SHARED table partial, so both pages get them.
//
// The load-bearing test here is Ws_admin_cannot_edit_a_key_of_another_workspace: like revoke, the
// edit is addressed by the key VALUE, so filtering the rendered list guards nothing — a forged POST
// naming another tenant's key must 404 AND leave the key byte-for-byte unchanged. The guard lives
// inside the UPDATE statement (AgentKeyAdminService.UpdateAsync), not in the rendered list.
public sealed class AgentKeyEditFixture : IAsyncLifetime
{
	public const string PasswordHash = "pbkdf2$100000$h1twJi/he3s8S7jSM9pkGQ==$efnLBffww5Gprn6BjpNgZkTcG+1zNu2L6z3TZ7YvD/o=";
	public const string Password = "test123";

	// A key declared in appsettings (Auth:ApiKeys) — NOT a DB row. CompositeApiKeyLookup asks config
	// first, so a stored edit of it could never take effect: the UI must REFUSE with a reason.
	public const string ConfigKey = "cfg_declared_key_value";

	public WebApplicationFactory<Program> Factory { get; }
	public HttpClient Client { get; private set; } = null!;

	public AgentKeyEditFixture()
	{
		Factory = new WebApplicationFactory<Program>()
			.WithWebHostBuilder(b =>
			{
				b.UseEnvironment("Testing");
				b.ConfigureAppConfiguration((_, cfg) =>
				{
					cfg.AddInMemoryCollection(new Dictionary<string, string?>
					{
						["ConnectionStrings:PetBox"] = TestSchema.NewTempConnectionString(),
						["Host:BackgroundServices"] = "false",
						["Features:Config"] = "true",
						["Admin:Username"] = "admin",
						["Admin:PasswordHash"] = PasswordHash,
						["Auth:ApiKeys:0:Key"] = ConfigKey,
						["Auth:ApiKeys:0:ProjectKey"] = "projea",
						["Auth:ApiKeys:0:Scopes"] = "config:read",
					});
				});
			});
	}

	public async Task InitializeAsync()
	{
		var cs = Factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("PetBox")!;
		TestSchema.Core(cs);
		Client = Factory.CreateClient(new WebApplicationFactoryClientOptions
		{
			AllowAutoRedirect = false,
			HandleCookies = false,
		});

		using var scope = Factory.Services.CreateScope();
		using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();

		await db.InsertAsync(new Workspace { Key = "wsea", Name = "Wsea", Description = "", CreatedAt = DateTime.UtcNow });
		await db.InsertAsync(new Workspace { Key = "wseb", Name = "Wseb", Description = "", CreatedAt = DateTime.UtcNow });
		await db.InsertAsync(new Project { Key = "projea", WorkspaceKey = "wsea", Name = "A", Description = "" });
		await db.InsertAsync(new Project { Key = "projeb", WorkspaceKey = "wseb", Name = "B", Description = "" });

		await AddUser(db, "edit-admin-a", "wsea", WorkspaceRole.Admin);
		await AddUser(db, "edit-admin-b", "wseb", WorkspaceRole.Admin);

		// Both pages need at least one row to render a form the tests can take an antiforgery token from.
		await MintKeyAsync("projea", "edit-decoy-wsea");
	}

	static async Task AddUser(PetBoxDb db, string username, string ws, WorkspaceRole role)
	{
		var id = await db.InsertWithInt64IdentityAsync(new User
		{
			Username = username,
			PasswordHash = PasswordHash,
			CreatedAt = DateTime.UtcNow,
		});
		await db.InsertAsync(new WorkspaceMember { UserId = id, WorkspaceKey = ws, Role = role });
	}

	public async Task<string> MintKeyAsync(
		string projectKey, string name, string scopes = "config:read",
		string? defaultProjectKey = null, DateTime? lastUsedAt = null)
	{
		var key = $"yb_key_{Guid.NewGuid():N}";
		using var scope = Factory.Services.CreateScope();
		using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();
		await db.InsertAsync(new ApiKey
		{
			Key = key,
			ProjectKey = projectKey,
			Scopes = scopes,
			Name = name,
			CreatedAt = DateTime.UtcNow,
			DefaultProjectKey = defaultProjectKey,
			LastUsedAt = lastUsedAt,
		});
		return key;
	}

	public ApiKey Row(string key)
	{
		using var scope = Factory.Services.CreateScope();
		using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();
		return db.ApiKeys.First(k => k.Key == key);
	}

	// The in-memory stamp the auth hot path writes. The admin list must MERGE it with the stored
	// column (the flusher only persists every ~5 min), so a key used seconds ago must not read "never".
	public void StampInMemory(string key) =>
		Factory.Services.GetRequiredService<IKeyStatService>().Stamp(key);

	public async Task DisposeAsync()
	{
		Client.Dispose();
		await Factory.DisposeAsync();
	}
}

public sealed class AgentKeyEditTests(AgentKeyEditFixture fx) : IClassFixture<AgentKeyEditFixture>
{
	readonly AgentKeyEditFixture _fx = fx;

	const string WseaKeys = "/ui/admin/ws/wsea/agent-keys";
	const string SysKeys = "/ui/admin/sys/agent-keys";

	static (string Token, string Cookie) ExtractAntiforgery(HttpResponseMessage resp, string html)
	{
		var tokenStart = html.IndexOf("__RequestVerificationToken", StringComparison.Ordinal);
		var valueStart = html.IndexOf("value=\"", tokenStart, StringComparison.Ordinal) + 7;
		var valueEnd = html.IndexOf('"', valueStart);
		var token = html[valueStart..valueEnd];
		var cookie = resp.Headers.GetValues("Set-Cookie")
			.First(c => c.Contains("Antiforgery", StringComparison.OrdinalIgnoreCase))
			.Split(';')[0];
		return (token, cookie);
	}

	async Task<string> LoginAsync(string username)
	{
		var loginPage = await _fx.Client.GetAsync("/Login");
		var (token, afCookie) = ExtractAntiforgery(loginPage, await loginPage.Content.ReadAsStringAsync());

		var req = new HttpRequestMessage(HttpMethod.Post, "/Login")
		{
			Content = new FormUrlEncodedContent(new Dictionary<string, string>
			{
				["username"] = username,
				["password"] = AgentKeyEditFixture.Password,
				["__RequestVerificationToken"] = token,
			}),
		};
		req.Headers.Add("Cookie", afCookie);
		using var resp = await _fx.Client.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.Redirect, $"login as '{username}' must succeed");
		return resp.Headers.GetValues("Set-Cookie")
			.First(c => c.StartsWith(".AspNetCore.Cookies", StringComparison.OrdinalIgnoreCase))
			.Split(';')[0];
	}

	async Task<HttpResponseMessage> GetAsync(string url, string authCookie)
	{
		var req = new HttpRequestMessage(HttpMethod.Get, url);
		req.Headers.Add("Cookie", authCookie);
		return await _fx.Client.SendAsync(req);
	}

	// The antiforgery token comes from a page the caller MAY open (their own). The forged-edit test
	// then aims that VALID token at someone else's key — exactly what an attacker can do. The guard
	// must therefore not be the antiforgery token, and must not be the rendered list either.
	async Task<HttpResponseMessage> PostEditAsync(
		string pageUrl, string authCookie, string key,
		string name, IEnumerable<string> scopes, string? defaultProject = null)
	{
		var getReq = new HttpRequestMessage(HttpMethod.Get, pageUrl);
		getReq.Headers.Add("Cookie", authCookie);
		using var getResp = await _fx.Client.SendAsync(getReq);
		getResp.StatusCode.Should().Be(HttpStatusCode.OK, "the attacker's own page must render — that is where the token comes from");
		var (token, afCookie) = ExtractAntiforgery(getResp, await getResp.Content.ReadAsStringAsync());

		var form = new List<KeyValuePair<string, string>>
		{
			new("key", key),
			new("name", name),
			new("__RequestVerificationToken", token),
		};
		form.AddRange(scopes.Select(s => new KeyValuePair<string, string>("scopes", s)));
		if (defaultProject is not null)
			form.Add(new KeyValuePair<string, string>("defaultProject", defaultProject));

		var req = new HttpRequestMessage(HttpMethod.Post, $"{pageUrl}?handler=Edit")
		{
			Content = new FormUrlEncodedContent(form),
		};
		req.Headers.Add("Cookie", $"{authCookie}; {afCookie}");
		return await _fx.Client.SendAsync(req);
	}

	// ---- the editor: rename / re-scope / set + CLEAR defaultProject (spec apikey-mutable) ----

	[Fact]
	public async Task Sysadmin_renames_a_key()
	{
		var key = await _fx.MintKeyAsync("projea", "before-rename");

		var auth = await LoginAsync("admin");
		using var resp = await PostEditAsync(SysKeys, auth, key, "after-rename", ["config:read"]);

		resp.StatusCode.Should().Be(HttpStatusCode.Redirect, "PRG back to the key list");
		_fx.Row(key).Name.Should().Be("after-rename");
	}

	[Fact]
	public async Task Sysadmin_replaces_the_scope_set()
	{
		var key = await _fx.MintKeyAsync("projea", "rescope-me", scopes: "config:read");

		var auth = await LoginAsync("admin");
		using var resp = await PostEditAsync(SysKeys, auth, key, "rescope-me", ["tasks:read", "tasks:write"]);

		resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
		// scopes REPLACE, they are not additive — the old config:read must be gone.
		_fx.Row(key).Scopes.Should().Be("tasks:read,tasks:write");
	}

	[Fact]
	public async Task Sysadmin_sets_the_default_project_on_a_cross_project_key()
	{
		var key = await _fx.MintKeyAsync("*", "set-default-project");
		_fx.Row(key).DefaultProjectKey.Should().BeNull("precondition: it starts with no default");

		var auth = await LoginAsync("admin");
		using var resp = await PostEditAsync(SysKeys, auth, key, "set-default-project", ["config:read"], defaultProject: "projea");

		resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
		_fx.Row(key).DefaultProjectKey.Should().Be("projea");
	}

	// The classic place where "" is not null: an empty form field must CLEAR the column, not store
	// an empty string (which would resolve to a project named "" on the next call).
	[Fact]
	public async Task Sysadmin_clears_the_default_project_and_it_becomes_null_not_empty_string()
	{
		var key = await _fx.MintKeyAsync("*", "clear-default-project", defaultProjectKey: "projea");
		_fx.Row(key).DefaultProjectKey.Should().Be("projea", "precondition: it starts WITH a default");

		var auth = await LoginAsync("admin");
		using var resp = await PostEditAsync(SysKeys, auth, key, "clear-default-project", ["config:read"], defaultProject: "");

		resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var row = _fx.Row(key);
		row.DefaultProjectKey.Should().BeNull("an empty field CLEARS the default project");
		row.DefaultProjectKey.Should().NotBe("", "and it must be NULL — an empty string is a different, broken value");
	}

	// ---- the "last used" column (spec apikey-last-used) ----

	[Fact]
	public async Task An_unused_key_reads_never_not_an_empty_cell_and_not_1970()
	{
		var key = await _fx.MintKeyAsync("projea", "never-used-key", lastUsedAt: null);

		var auth = await LoginAsync("admin");
		using var resp = await GetAsync(SysKeys, auth);
		var html = await resp.Content.ReadAsStringAsync();

		html.Should().Contain("agent-key-never-used",
			"an unused key must SAY 'never' — not render an empty cell the reader has to interpret");
		html.Should().NotContain("1970-01-01", "epoch-0 is not 'never'; a default DateTime must never reach the page");
		html.Should().NotContain("0001-01-01", "and neither is DateTime.MinValue");
		_fx.Row(key).LastUsedAt.Should().BeNull();
	}

	[Fact]
	public async Task A_used_key_shows_the_moment_it_was_last_used()
	{
		var usedAt = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
		await _fx.MintKeyAsync("projea", "used-key", lastUsedAt: usedAt);

		var auth = await LoginAsync("admin");
		using var resp = await GetAsync(SysKeys, auth);
		var html = await resp.Content.ReadAsStringAsync();

		html.Should().Contain("2026-03-04 05:06", "the stored moment must render in the last-used column");
	}

	// The flusher only persists every ~5 minutes, so the COLUMN alone lies for that window: a key
	// used seconds ago would read "never". The page must merge the in-memory stamp and take the later.
	[Fact]
	public async Task A_key_used_seconds_ago_does_not_still_read_never()
	{
		var key = await _fx.MintKeyAsync("projea", "just-used-key", lastUsedAt: null);
		_fx.StampInMemory(key);

		var auth = await LoginAsync("admin");
		using var resp = await GetAsync(SysKeys, auth);
		var html = await resp.Content.ReadAsStringAsync();

		var row = ExtractRow(html, "just-used-key");
		row.Should().NotContain("agent-key-never-used",
			"the stored column trails by up to a flush window — the page must merge the live in-memory stamp");
		row.Should().Contain("agent-key-last-used");
		_fx.Row(key).LastUsedAt.Should().BeNull("and it must do so WITHOUT writing to the DB on a render");
	}

	// The <tr> of one key — the whole page contains every key's markup, so a per-row assertion has
	// to look at that key's row and nothing else.
	static string ExtractRow(string html, string keyName)
	{
		var at = html.IndexOf(keyName, StringComparison.Ordinal);
		at.Should().BeGreaterThan(-1, $"key '{keyName}' must be listed");
		var start = html.LastIndexOf("<tr", at, StringComparison.Ordinal);
		var end = html.IndexOf("</tr>", at, StringComparison.Ordinal);
		return html[start..end];
	}

	// ---- workspace confinement: the guard is in the UPDATE, not in the rendered list ----

	// THE security test. A ws admin holds a valid session and a valid antiforgery token for their OWN
	// page; nothing stops them POSTing another workspace's key value. The edit must not happen.
	[Fact]
	public async Task Ws_admin_cannot_edit_a_key_of_another_workspace()
	{
		var victim = await _fx.MintKeyAsync("projeb", "victim-key-wseb", scopes: "config:read");

		var auth = await LoginAsync("edit-admin-a");
		using var resp = await PostEditAsync(WseaKeys, auth, victim, "pwned", ["admin:provision"]);

		resp.StatusCode.Should().Be(HttpStatusCode.NotFound,
			"a forged POST naming another workspace's key must not be honoured (404 — not even a hint that it exists)");

		var row = _fx.Row(victim);
		row.Name.Should().Be("victim-key-wseb", "the key must survive the forged edit UNCHANGED");
		row.Scopes.Should().Be("config:read", "and above all it must not have been re-scoped to admin:provision");
	}

	// A "*" key belongs to no single workspace — a ws admin must not be able to touch it either.
	[Fact]
	public async Task Ws_admin_cannot_edit_a_cross_project_key()
	{
		var wildcard = await _fx.MintKeyAsync("*", "victim-wildcard-edit", scopes: "config:read");

		var auth = await LoginAsync("edit-admin-a");
		using var resp = await PostEditAsync(WseaKeys, auth, wildcard, "pwned", ["admin:provision"]);

		resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
		var row = _fx.Row(wildcard);
		row.Name.Should().Be("victim-wildcard-edit");
		row.Scopes.Should().Be("config:read");
	}

	[Fact]
	public async Task Ws_admin_edits_their_own_key()
	{
		var mine = await _fx.MintKeyAsync("projea", "own-key-before", scopes: "config:read");

		var auth = await LoginAsync("edit-admin-a");
		using var resp = await PostEditAsync(WseaKeys, auth, mine, "own-key-after", ["tasks:read"]);

		resp.StatusCode.Should().Be(HttpStatusCode.Redirect, "PRG back to the ws key list");
		resp.Headers.Location!.ToString().Should().NotContain("/AccessDenied");
		var row = _fx.Row(mine);
		row.Name.Should().Be("own-key-after");
		row.Scopes.Should().Be("tasks:read");
	}

	// ---- config-declared keys: a REASON, never a silent no-op (spec apikey-mutable) ----

	[Fact]
	public async Task A_config_declared_key_is_not_listed_in_the_admin_table()
	{
		var auth = await LoginAsync("admin");
		using var resp = await GetAsync(SysKeys, auth);

		(await resp.Content.ReadAsStringAsync()).Should().NotContain(AgentKeyEditFixture.ConfigKey,
			"config keys are not DB rows — they have no editable row in the UI at all");
	}

	// Config wins on every auth lookup, so a stored edit of a config key would change NOTHING while
	// looking like it worked. That silent no-op is the failure mode being closed: refuse, and say why.
	[Fact]
	public async Task Editing_a_config_declared_key_is_refused_with_a_reason()
	{
		var auth = await LoginAsync("admin");
		using var resp = await PostEditAsync(SysKeys, auth, AgentKeyEditFixture.ConfigKey, "renamed-cfg", ["config:read"]);

		resp.StatusCode.Should().Be(HttpStatusCode.Redirect, "the form comes back to the page carrying the refusal");

		// The reason must reach the user — a redirect that says nothing IS the silent failure.
		var landing = await GetAsync(SysKeys, auth + $"; {ExtractTempDataCookie(resp)}");
		var html = await landing.Content.ReadAsStringAsync();
		landing.Dispose();

		html.Should().Contain("notice-error", "the refusal must be shown, not swallowed");
		html.Should().Contain("configuration", "and it must say WHY: the key is declared in configuration");

		using var scope = _fx.Factory.Services.CreateScope();
		using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();
		db.ApiKeys.Any(k => k.Key == AgentKeyEditFixture.ConfigKey).Should().BeFalse(
			"and no phantom DB row may be written for a config key");
	}

	// TempData rides the redirect in a cookie; the landing GET must carry it back to render the notice.
	static string ExtractTempDataCookie(HttpResponseMessage resp) =>
		resp.Headers.TryGetValues("Set-Cookie", out var cookies)
			? cookies.FirstOrDefault(c => c.Contains("TempData", StringComparison.OrdinalIgnoreCase))?.Split(';')[0] ?? ""
			: "";

	[Fact]
	public async Task An_edit_with_no_scopes_is_refused_and_the_key_keeps_its_scopes()
	{
		var key = await _fx.MintKeyAsync("projea", "keep-my-scopes", scopes: "config:read");

		var auth = await LoginAsync("admin");
		using var resp = await PostEditAsync(SysKeys, auth, key, "keep-my-scopes", []);

		resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
		_fx.Row(key).Scopes.Should().Be("config:read", "a key with zero scopes is useless — the edit must be refused");
	}
}
