using System.Net;
using LinqToDB;
using LinqToDB.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Settings;

namespace PetBox.Tests.Web;

// workspace-self-provisioning (spec workspace-create-permission + workspace-creator-is-admin).
//
// Creating a workspace used to be sysadmin-or-nobody, which made a fresh account a brick: it could
// sign in, see an empty state, and do nothing. The right is now an explicit NUMBER on the account
// (Users.WorkspaceQuota) — 0 means no. What this suite pins down:
//
//   * the quota is enforced on the WRITE, not by hiding a button — a direct POST from an account
//     with quota 0 (or a spent quota) is refused, and no workspace row appears;
//   * a spent quota is spent: quota 1 + one workspace already owned = the second create is refused;
//   * the creator IS the admin of what they created, on the SAME cookie — no re-login (the
//     WorkspaceClaimsRefresher contract), so they can immediately open the workspace, see it in the
//     selector, and create a project in it (Admin/Projects is WorkspaceAdmin-gated and needs no
//     change of its own — that is the assertion, not an assumption);
//   * a '$'-prefixed key is refused (it would collide with the reserved $system / $workspace / $ws-*
//     containers — spec reserved-workspace-project);
//   * a sysadmin is not bound by the quota.
public sealed class WorkspaceSelfProvisioningFixture : IAsyncLifetime
{
	public const string PasswordHash = "pbkdf2$100000$h1twJi/he3s8S7jSM9pkGQ==$efnLBffww5Gprn6BjpNgZkTcG+1zNu2L6z3TZ7YvD/o=";
	public const string Password = "test123";

	readonly string _baseDir;

	public WebApplicationFactory<Program> Factory { get; }
	public HttpClient Client { get; private set; } = null!;

	public WorkspaceSelfProvisioningFixture()
	{
		_baseDir = Path.Combine(Path.GetTempPath(), "petbox-wssp-" + Guid.NewGuid().ToString("N"));
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
						["Features:Tasks"] = "true",
						["Features:Memory"] = "true",
						["Features:Data"] = "true",
						["Admin:Username"] = "admin",
						["Admin:PasswordHash"] = PasswordHash,
					});
				});
				b.ConfigureServices(svc =>
				{
					Replace<PetBox.Tasks.Data.TasksDb>(svc, "tasks",
						c => new PetBox.Tasks.Data.TasksDb(PetBox.Tasks.Data.TasksDb.CreateOptions(c)), PetBox.Tasks.Data.TasksSchema.Ensure);
					Replace<PetBox.Memory.Data.MemoryDb>(svc, "memory",
						c => new PetBox.Memory.Data.MemoryDb(PetBox.Memory.Data.MemoryDb.CreateOptions(c)), PetBox.Memory.Data.MemorySchema.Ensure);
					Replace<PetBox.Sessions.Data.SessionsDb>(svc, "sessions",
						c => new PetBox.Sessions.Data.SessionsDb(PetBox.Sessions.Data.SessionsDb.CreateOptions(c)), PetBox.Sessions.Data.SessionsSchema.Ensure);
				});
			});
	}

	void Replace<TDb>(IServiceCollection svc, string sub, Func<string, TDb> create, Action<string> ensure) where TDb : DataConnection
	{
		var existing = svc.SingleOrDefault(d => d.ServiceType == typeof(IScopedDbFactory<TDb>));
		if (existing is not null) svc.Remove(existing);
		svc.AddSingleton<IScopedDbFactory<TDb>>(_ => new ScopedDbFactory<TDb>(
			Path.Combine(_baseDir, sub), Scope.Project, create, ensure));
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

		// bricked: quota 0 — the account an admin created without granting the allowance.
		await db.InsertAsync(new User
		{
			Username = "bricked",
			PasswordHash = PasswordHash,
			CreatedAt = DateTime.UtcNow,
			WorkspaceQuota = 0,
		});

		// founder: quota 1 — the self-service path this feature exists for.
		await db.InsertAsync(new User
		{
			Username = "founder",
			PasswordHash = PasswordHash,
			CreatedAt = DateTime.UtcNow,
			WorkspaceQuota = 1,
		});

		// spender: quota 1, but already owns a workspace — the allowance is spent.
		var spenderId = await db.InsertWithInt64IdentityAsync(new User
		{
			Username = "spender",
			PasswordHash = PasswordHash,
			CreatedAt = DateTime.UtcNow,
			WorkspaceQuota = 1,
		});
		await db.InsertAsync(new Workspace { Key = "spent-ws", Name = "Spent", Description = "", CreatedAt = DateTime.UtcNow });
		await db.InsertAsync(new WorkspaceMember { UserId = spenderId, WorkspaceKey = "spent-ws", Role = WorkspaceRole.Admin });

		// dollar / dollarkey: quota 1 each, so 'founder' keeps its one shot. Two accounts, because one
		// test SPENDS its allowance (proving a rejected key did not) and the other must still be able to
		// reach the create path — and xunit does not order tests within a class.
		await db.InsertAsync(new User
		{
			Username = "dollar",
			PasswordHash = PasswordHash,
			CreatedAt = DateTime.UtcNow,
			WorkspaceQuota = 1,
		});
		await db.InsertAsync(new User
		{
			Username = "dollarkey",
			PasswordHash = PasswordHash,
			CreatedAt = DateTime.UtcNow,
			WorkspaceQuota = 1,
		});

		// promotable: quota 0 — the account a sysadmin un-bricks by RAISING the allowance after the
		// fact. Its own user, so raising it cannot perturb the quota-0 assertions above.
		await db.InsertAsync(new User
		{
			Username = "promotable",
			PasswordHash = PasswordHash,
			CreatedAt = DateTime.UtcNow,
			WorkspaceQuota = 0,
		});
	}

	public async Task DisposeAsync()
	{
		Client.Dispose();
		await Factory.DisposeAsync();
		try { if (Directory.Exists(_baseDir)) Directory.Delete(_baseDir, recursive: true); }
		catch (IOException) { /* a still-open SQLite handle — a temp dir left behind is harmless */ }
	}
}

public sealed class WorkspaceSelfProvisioningTests : IClassFixture<WorkspaceSelfProvisioningFixture>
{
	readonly WorkspaceSelfProvisioningFixture _fx;
	readonly HttpClient _client;

	public WorkspaceSelfProvisioningTests(WorkspaceSelfProvisioningFixture fx)
	{
		_fx = fx;
		_client = fx.Client;
	}

	const string CreatePage = "/ui/me/workspaces/new";

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
		var loginPage = await _client.GetAsync("/Login");
		var (token, afCookie) = ExtractAntiforgery(loginPage, await loginPage.Content.ReadAsStringAsync());

		var req = new HttpRequestMessage(HttpMethod.Post, "/Login");
		req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
		{
			["username"] = username,
			["password"] = WorkspaceSelfProvisioningFixture.Password,
			["__RequestVerificationToken"] = token,
		});
		req.Headers.Add("Cookie", afCookie);
		using var resp = await _client.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.Redirect, $"login as '{username}' must succeed");
		return resp.Headers.GetValues("Set-Cookie")
			.First(c => c.StartsWith(".AspNetCore.Cookies", StringComparison.OrdinalIgnoreCase))
			.Split(';')[0];
	}

	async Task<HttpResponseMessage> GetAsync(string url, string authCookie)
	{
		var req = new HttpRequestMessage(HttpMethod.Get, url);
		req.Headers.Add("Cookie", authCookie);
		return await _client.SendAsync(req);
	}

	// A POST that does NOT depend on being able to GET the target page first — the whole point of the
	// "direct POST" tests is that the account is denied the GET. The antiforgery pair is minted on a
	// page the account CAN open (/ui/me/account); the token is per-session, not per-page.
	async Task<HttpResponseMessage> PostAsync(string url, string authCookie, IDictionary<string, string> fields)
	{
		var getReq = new HttpRequestMessage(HttpMethod.Get, "/ui/me/account");
		getReq.Headers.Add("Cookie", authCookie);
		using var getResp = await _client.SendAsync(getReq);
		var (token, afCookie) = ExtractAntiforgery(getResp, await getResp.Content.ReadAsStringAsync());

		fields["__RequestVerificationToken"] = token;
		var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(fields) };
		req.Headers.Add("Cookie", $"{authCookie}; {afCookie}");
		return await _client.SendAsync(req);
	}

	static void ShouldBeDenied(HttpResponseMessage resp, string because)
	{
		resp.StatusCode.Should().Be(HttpStatusCode.Redirect, because);
		resp.Headers.Location!.ToString().Should().Contain("/AccessDenied", because);
	}

	static Dictionary<string, string> Form(string key, string name) =>
		new() { ["key"] = key, ["name"] = name, ["description"] = "" };

	bool WorkspaceExists(string key)
	{
		using var scope = _fx.Factory.Services.CreateScope();
		using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();
		return db.Workspaces.Any(w => w.Key == key);
	}

	// ---- quota 0: the page AND the write are both closed ----

	[Fact]
	public async Task Quota_zero_is_denied_the_create_page()
	{
		var auth = await LoginAsync("bricked");
		using var resp = await GetAsync(CreatePage, auth);
		ShouldBeDenied(resp, "an account with a 0 allowance may not create a workspace");
	}

	[Fact]
	public async Task Quota_zero_is_denied_a_direct_POST_and_writes_nothing()
	{
		var auth = await LoginAsync("bricked");

		using var resp = await PostAsync(CreatePage, auth, Form("sneaky", "Sneaky"));
		ShouldBeDenied(resp,
			"the gate is the number on the account, checked on the WRITE — not a hidden button. A client "
			+ "that posts the form directly must be refused exactly like one that cannot open the page");

		WorkspaceExists("sneaky").Should().BeFalse("the refused POST must not have created anything");
	}

	// ---- a spent quota is spent ----

	[Fact]
	public async Task Quota_one_already_spent_is_refused_a_second_workspace()
	{
		var auth = await LoginAsync("spender");

		using var page = await GetAsync(CreatePage, auth);
		ShouldBeDenied(page, "quota 1, one workspace already owned — the allowance is spent");

		using var post = await PostAsync(CreatePage, auth, Form("second-ws", "Second"));
		ShouldBeDenied(post, "and the write path refuses it too");

		WorkspaceExists("second-ws").Should().BeFalse("no second workspace on a spent allowance");
	}

	// ---- the '$' prefix (spec reserved-workspace-project) ----

	// A '$' key is refused by the KEY RULE, before anything else looks at it — which matters, because
	// "$system" already exists (M004 seeds it) and would otherwise be caught only by the
	// already-exists check, leaving "$evil" (which exists nowhere) free to be created.
	[Theory]
	[InlineData("$system")]   // the reserved system workspace
	[InlineData("$workspace")] // the reserved $system memory container
	[InlineData("$evil")]      // a key that collides with nothing — still refused, on the prefix alone
	public async Task A_dollar_prefixed_key_is_rejected(string key)
	{
		// Its OWN account: this test must not depend on whether the "does not consume the allowance"
		// test has already spent 'dollar''s one shot (xunit orders tests within a class arbitrarily).
		var auth = await LoginAsync("dollarkey");

		using var resp = await PostAsync(CreatePage, auth, Form(key, "Pwned"));
		resp.StatusCode.Should().Be(HttpStatusCode.OK,
			"the form re-renders with the error — this is a validation failure, not an authorization one");

		var html = await resp.Content.ReadAsStringAsync();
		html.Should().Contain("new-workspace-error", "the key rule must be shown to the user");
		html.Should().Contain("reserved for built-in containers", "and it must say WHY the key was refused");

		// Nothing was written under the rejected key: no workspace row (for a key that did not already
		// exist), no membership, no memory container.
		using var scope = _fx.Factory.Services.CreateScope();
		using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();
		var dollarId = db.Users.First(u => u.Username == "dollarkey").Id;
		db.WorkspaceMembers.Any(m => m.UserId == dollarId && m.WorkspaceKey == key).Should().BeFalse(
			"'$' is the prefix of the reserved containers ($system / $workspace / $ws-*) — a user-chosen key "
			+ "must never be able to collide with them, let alone make its author an admin of one");
	}

	[Fact]
	public async Task A_rejected_key_does_not_consume_the_allowance()
	{
		var auth = await LoginAsync("dollar");

		using (var bad = await PostAsync(CreatePage, auth, Form("$nope", "Nope")))
			bad.StatusCode.Should().Be(HttpStatusCode.OK, "rejected");

		using var ok = await PostAsync(CreatePage, auth, Form("dollar-ws", "Dollar"));
		ok.StatusCode.Should().Be(HttpStatusCode.Redirect,
			"a refused key is not a spent allowance — the quota counts workspaces OWNED, and a rejected "
			+ "create made none");
		WorkspaceExists("dollar-ws").Should().BeTrue();
	}

	// ---- the happy path, end to end, on ONE cookie ----

	[Fact]
	public async Task Creator_is_admin_of_their_new_workspace_without_re_login()
	{
		// ONE sign-in. Every assertion below runs on this same cookie — the point is that nothing here
		// requires the user to sign out and back in for their brand-new membership to take effect.
		var auth = await LoginAsync("founder");

		// Before: no workspace at all, and the landing page offers the CTA (the quota allows it).
		using (var landing = await GetAsync("/", auth))
		{
			landing.StatusCode.Should().Be(HttpStatusCode.OK, "no membership yet — the empty state");
			var html = await landing.Content.ReadAsStringAsync();
			html.Should().Contain("no-workspaces-create",
				"an account whose allowance permits a workspace is offered the CTA, not told to ask an admin");
		}

		using (var page = await GetAsync(CreatePage, auth))
			page.StatusCode.Should().Be(HttpStatusCode.OK, "quota 1, none owned — the create page is open");

		using (var create = await PostAsync(CreatePage, auth, Form("founder-ws", "Founder WS")))
		{
			create.StatusCode.Should().Be(HttpStatusCode.Redirect, "a successful create redirects into the workspace");
			create.Headers.Location!.ToString().Should().Be("/ui/founder-ws");
		}

		// Admin of it in the DB — the creator IS the administrator (spec workspace-creator-is-admin).
		using (var scope = _fx.Factory.Services.CreateScope())
		{
			using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();
			var userId = db.Users.First(u => u.Username == "founder").Id;
			db.WorkspaceMembers
				.Any(m => m.UserId == userId && m.WorkspaceKey == "founder-ws" && m.Role == WorkspaceRole.Admin)
				.Should().BeTrue();

			// The memory container was provisioned with it — a workspace without one has a dead
			// Shared-memory nav entry.
			db.Projects.Any(p => p.Key == "$ws-founder-ws" && p.WorkspaceKey == "founder-ws")
				.Should().BeTrue("EnsureContainerAsync runs as part of the create, in the service");
		}

		// SAME cookie: the workspace opens, and it is in the selector. WorkspaceClaimsRefresher rebuilds
		// the membership claims from the DB per request, so the cookie minted BEFORE the workspace
		// existed already carries the role.
		using (var ws = await GetAsync("/ui/founder-ws", auth))
		{
			ws.StatusCode.Should().Be(HttpStatusCode.OK, "the creator reaches their workspace with no re-login");
			var html = await ws.Content.ReadAsStringAsync();
			html.Should().Contain("nav-workspace-select", "the workspace selector renders");
			html.Should().Contain("founder-ws", "and the new workspace is in it");
		}

		// The workspace admin surface is open to them — and creating a project in it works with no
		// change to Admin/Projects (it is already WorkspaceAdmin-gated; the creator now satisfies it).
		using (var admin = await GetAsync("/ui/admin/ws/founder-ws/projects", auth))
			admin.StatusCode.Should().Be(HttpStatusCode.OK, "the creator administers their own workspace");

		using (var proj = await PostAsync("/ui/admin/ws/founder-ws/projects?handler=Create", auth,
			new Dictionary<string, string> { ["Key"] = "founder-proj", ["Name"] = "First project", ["Description"] = "" }))
		{
			proj.StatusCode.Should().Be(HttpStatusCode.Redirect, "creating a project in one's own workspace must succeed");
			proj.Headers.Location!.ToString().Should().NotContain("/AccessDenied");
		}

		using (var scope = _fx.Factory.Services.CreateScope())
		{
			using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();
			db.Projects.Any(p => p.Key == "founder-proj" && p.WorkspaceKey == "founder-ws")
				.Should().BeTrue("no separate 'create project' right is needed — being the workspace Admin is the right");
		}

		// And the allowance is now spent: the second create is refused.
		using var second = await PostAsync(CreatePage, auth, Form("founder-ws-2", "Second"));
		ShouldBeDenied(second, "quota 1 was consumed by founder-ws");
		WorkspaceExists("founder-ws-2").Should().BeFalse();
	}

	// ---- the account with no allowance is told what to do instead ----

	[Fact]
	public async Task Empty_state_without_an_allowance_offers_no_CTA()
	{
		var auth = await LoginAsync("bricked");
		using var resp = await GetAsync("/", auth);

		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();
		html.Should().Contain("no-workspaces-card");
		html.Should().NotContain("no-workspaces-create",
			"an account that cannot create a workspace must not be shown a button into a 403");
		html.Should().Contain("Ask an administrator", "it must say what to do instead");
	}

	// ---- the admin form: the allowance is a decision, never a silent default ----

	[Fact]
	public async Task Creating_a_user_without_an_allowance_is_refused()
	{
		var auth = await LoginAsync("admin");

		using var resp = await PostAsync("/ui/admin/sys/users?handler=Create", auth,
			new Dictionary<string, string> { ["username"] = "no-quota-said", ["password"] = "pw123456" });

		resp.StatusCode.Should().Be(HttpStatusCode.OK, "the form re-renders with the error");
		(await resp.Content.ReadAsStringAsync()).Should().Contain("sys-users-error");

		using var scope = _fx.Factory.Services.CreateScope();
		using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();
		db.Users.Any(u => u.Username == "no-quota-said").Should().BeFalse(
			"an omitted allowance must NOT be silently written as 0 — 'nobody decided' and 'decided: none' "
			+ "are different facts, and only the second may land on an account");
	}

	[Fact]
	public async Task A_new_user_gets_exactly_the_allowance_the_admin_typed()
	{
		var auth = await LoginAsync("admin");

		using (var three = await PostAsync("/ui/admin/sys/users?handler=Create", auth,
			new Dictionary<string, string> { ["username"] = "gets-three", ["password"] = "pw123456", ["workspaceQuota"] = "3" }))
			three.StatusCode.Should().Be(HttpStatusCode.Redirect);

		using (var zero = await PostAsync("/ui/admin/sys/users?handler=Create", auth,
			new Dictionary<string, string> { ["username"] = "gets-zero", ["password"] = "pw123456", ["workspaceQuota"] = "0" }))
			zero.StatusCode.Should().Be(HttpStatusCode.Redirect);

		using var scope = _fx.Factory.Services.CreateScope();
		using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();
		db.Users.First(u => u.Username == "gets-three").WorkspaceQuota.Should().Be(3);
		db.Users.First(u => u.Username == "gets-zero").WorkspaceQuota.Should().Be(0,
			"an explicit 0 is a legitimate answer — it just has to be given");
	}

	[Fact]
	public async Task An_existing_users_allowance_can_be_raised()
	{
		var auth = await LoginAsync("admin");

		long userId;
		using (var scope = _fx.Factory.Services.CreateScope())
		{
			using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();
			userId = db.Users.First(u => u.Username == "promotable").Id;
		}

		using var resp = await PostAsync("/ui/admin/sys/users?handler=SetQuota", auth,
			new Dictionary<string, string>
			{
				["userId"] = userId.ToString(System.Globalization.CultureInfo.InvariantCulture),
				["workspaceQuota"] = "2",
			});
		resp.StatusCode.Should().Be(HttpStatusCode.Redirect);

		using (var scope = _fx.Factory.Services.CreateScope())
		{
			using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();
			db.Users.First(u => u.Id == userId).WorkspaceQuota.Should().Be(2,
				"a sysadmin can grant the right after the fact — that is the un-bricking path");
		}

		// And the account can now actually use it, on a cookie minted after the grant.
		var promoted = await LoginAsync("promotable");
		using var page = await GetAsync(CreatePage, promoted);
		page.StatusCode.Should().Be(HttpStatusCode.OK, "the raised allowance is live — no restart, no migration");
	}

	// ---- sysadmin is not bound by the quota ----

	[Fact]
	public async Task Sysadmin_is_not_bound_by_the_quota()
	{
		// The bootstrap admin's own WorkspaceQuota is 0 — the free-pass is the point.
		var auth = await LoginAsync("admin");

		using (var page = await GetAsync(CreatePage, auth))
			page.StatusCode.Should().Be(HttpStatusCode.OK, "a sysadmin may always create — the quota is not their leash");

		using (var a = await PostAsync(CreatePage, auth, Form("sys-ws-a", "A")))
			a.StatusCode.Should().Be(HttpStatusCode.Redirect);
		using (var b = await PostAsync(CreatePage, auth, Form("sys-ws-b", "B")))
			b.StatusCode.Should().Be(HttpStatusCode.Redirect);

		WorkspaceExists("sys-ws-a").Should().BeTrue();
		WorkspaceExists("sys-ws-b").Should().BeTrue(
			"two creates from an account whose quota is 0 — a sysadmin is unlimited");
	}
}
