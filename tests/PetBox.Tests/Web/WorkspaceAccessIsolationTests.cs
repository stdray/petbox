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

// workspace-access-isolation + auth-denied-and-empty-state.
//
// Every workspace-scoped page used to carry a bare [Authorize] — authenticated was enough, so ANY
// signed-in user could read another tenant's dashboard, database rows, session transcripts, traces
// and the server-wide /ui/admin/sys counters just by typing the URL. They now carry
// WorkspaceViewer / SysAdmin, and each page binds the {projectKey} to the {workspaceKey} of the
// route (the policy proves membership of the workspace, not that the project lives in it).
//
// A denial is a 302 to /AccessDenied (a real 403 page) — never to /Login, which used to re-render
// the sign-in form to an already-signed-in user and loop.
public sealed class WorkspaceAccessIsolationFixture : IAsyncLifetime
{
	public const string PasswordHash = "pbkdf2$100000$h1twJi/he3s8S7jSM9pkGQ==$efnLBffww5Gprn6BjpNgZkTcG+1zNu2L6z3TZ7YvD/o=";
	public const string Password = "test123";

	readonly string _baseDir;

	public WebApplicationFactory<Program> Factory { get; }
	public HttpClient Client { get; private set; } = null!;

	public WorkspaceAccessIsolationFixture()
	{
		_baseDir = Path.Combine(Path.GetTempPath(), "petbox-wsiso-" + Guid.NewGuid().ToString("N"));
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
						// Bootstrap admin → the sysadmin free-pass this suite must not regress.
						["Admin:Username"] = "admin",
						["Admin:PasswordHash"] = PasswordHash,
					});
				});
				// Keep the Tasks/Memory/Sessions files out of the shared dev data dir.
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

		await db.InsertAsync(new Workspace { Key = "wsa", Name = "Wsa", Description = "", CreatedAt = DateTime.UtcNow });
		await db.InsertAsync(new Workspace { Key = "wsb", Name = "Wsb", Description = "", CreatedAt = DateTime.UtcNow });
		await db.InsertAsync(new Project { Key = "proja", WorkspaceKey = "wsa", Name = "A", Description = "" });
		await db.InsertAsync(new Project { Key = "projb", WorkspaceKey = "wsb", Name = "B", Description = "" });

		// nomad: a fresh Regular account — NO membership anywhere (the production symptom).
		await db.InsertAsync(new User { Username = "nomad", PasswordHash = PasswordHash, CreatedAt = DateTime.UtcNow });

		// eve: Member of wsa only.
		var eveId = await db.InsertWithInt64IdentityAsync(new User
		{
			Username = "eve-iso",
			PasswordHash = PasswordHash,
			CreatedAt = DateTime.UtcNow,
		});
		await db.InsertAsync(new WorkspaceMember { UserId = eveId, WorkspaceKey = "wsa", Role = WorkspaceRole.Member });

		// latecomer: no membership yet — one is granted mid-test to prove claims are not frozen
		// at sign-in (the invite path used to need a re-login).
		await db.InsertAsync(new User { Username = "latecomer", PasswordHash = PasswordHash, CreatedAt = DateTime.UtcNow });

		// viewer: Viewer (read-only) role in wsa — proves viewer-member-consistency: a Viewer reads
		// a board/log page but a MUTATION handler on that same page still requires Member+.
		var viewerId = await db.InsertWithInt64IdentityAsync(new User
		{
			Username = "viewer-iso",
			PasswordHash = PasswordHash,
			CreatedAt = DateTime.UtcNow,
		});
		await db.InsertAsync(new WorkspaceMember { UserId = viewerId, WorkspaceKey = "wsa", Role = WorkspaceRole.Viewer });

		// A real board in proja (wsa) so the mutation tests exercise the ROLE guard itself, not a
		// "board not found" 404 that would otherwise be indistinguishable from a denial.
		var boards = scope.ServiceProvider.GetRequiredService<PetBox.Tasks.Data.ITaskBoardStore>();
		await boards.EnsureAsync("proja", "board1");
	}

	public async Task DisposeAsync()
	{
		Client.Dispose();
		await Factory.DisposeAsync();
		try { if (Directory.Exists(_baseDir)) Directory.Delete(_baseDir, recursive: true); }
		catch (IOException) { /* a still-open SQLite handle — a temp dir left behind is harmless */ }
	}
}

public sealed class WorkspaceAccessIsolationTests : IClassFixture<WorkspaceAccessIsolationFixture>
{
	readonly WorkspaceAccessIsolationFixture _fx;
	readonly HttpClient _client;

	public WorkspaceAccessIsolationTests(WorkspaceAccessIsolationFixture fx)
	{
		_fx = fx;
		_client = fx.Client;
	}

	// Every workspace-scoped page that used to carry a bare [Authorize], as {ws}/{project} templates.
	public static TheoryData<string> ProtectedPages()
	{
		var data = new TheoryData<string>();
		data.Add("/ui/{ws}");
		data.Add("/ui/{ws}/{p}");
		data.Add("/ui/{ws}/{p}/databases");
		data.Add("/ui/{ws}/{p}/databases/main");
		data.Add("/ui/{ws}/{p}/databases/main/rows");
		data.Add("/ui/{ws}/{p}/tasks");
		data.Add("/ui/{ws}/{p}/sessions");
		data.Add("/ui/{ws}/{p}/sessions/abc123");
		data.Add("/ui/{ws}/{p}/traces");
		data.Add("/ui/{ws}/{p}/traces/deadbeef");
		// same-class-cross-tenant-field-id-4c0359 tail: these three shipped with a bare
		// [Authorize(Policy = "WorkspaceMember")] but NO project↔route-workspace bind at all.
		data.Add("/ui/{ws}/{p}/logs");
		data.Add("/ui/{ws}/{p}/tasks/board1");
		data.Add("/ui/{ws}/{p}/tasks/board1/anyslug");
		return data;
	}

	static string ForWsa(string template) => template.Replace("{ws}", "wsa", StringComparison.Ordinal).Replace("{p}", "proja", StringComparison.Ordinal);

	static string ForWsb(string template) => template.Replace("{ws}", "wsb", StringComparison.Ordinal).Replace("{p}", "projb", StringComparison.Ordinal);

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
			["password"] = WorkspaceAccessIsolationFixture.Password,
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

	static void ShouldBeDenied(HttpResponseMessage resp, string because)
	{
		resp.StatusCode.Should().Be(HttpStatusCode.Redirect, because);
		var location = resp.Headers.Location!.ToString();
		location.Should().Contain("/AccessDenied", because);
		location.Should().NotContain("/Login",
			"a 403 must never be dressed up as a sign-in prompt — that was the redirect loop");
	}

	[Theory]
	[MemberData(nameof(ProtectedPages))]
	public async Task Regular_without_any_membership_is_denied_every_workspace_page(string template)
	{
		var auth = await LoginAsync("nomad");
		using var resp = await GetAsync(ForWsa(template), auth);
		ShouldBeDenied(resp, $"a user with no membership must not read {template}");
	}

	[Theory]
	[MemberData(nameof(ProtectedPages))]
	public async Task Member_of_wsa_is_denied_the_same_page_in_wsb(string template)
	{
		var auth = await LoginAsync("eve-iso");
		using var resp = await GetAsync(ForWsb(template), auth);
		ShouldBeDenied(resp, $"a member of wsa must not read wsb's {template}");
	}

	// The positive side of the gate: the page is REACHED (rendered or a legitimate 404 for a
	// nonexistent session/trace id) — never an authorization redirect.
	static void ShouldBeReached(HttpResponseMessage resp, string because) =>
		resp.StatusCode.Should().BeOneOf([HttpStatusCode.OK, HttpStatusCode.NotFound], because);

	[Theory]
	[MemberData(nameof(ProtectedPages))]
	public async Task Member_of_wsa_can_still_open_wsa(string template)
	{
		var auth = await LoginAsync("eve-iso");
		using var resp = await GetAsync(ForWsa(template), auth);
		ShouldBeReached(resp, $"a member of wsa must keep access to {template}");
	}

	[Theory]
	[MemberData(nameof(ProtectedPages))]
	public async Task Sysadmin_keeps_the_free_pass_everywhere(string template)
	{
		var auth = await LoginAsync("admin");
		using var resp = await GetAsync(ForWsb(template), auth);
		ShouldBeReached(resp, $"sysadmin implicitly views every workspace — {template}");
	}

	// The project is bound to the ROUTE workspace: membership in wsa is not a licence to read
	// wsb's project by pointing wsa's URL at it. Enforced by the global
	// ProjectWorkspaceBindingFilter (Program.cs) BEFORE any page handler runs, so the mismatch is
	// a hard 404 — not the in-page "not found" banner a page's own null-check would render.
	[Theory]
	[InlineData("/ui/wsa/projb")]
	[InlineData("/ui/wsa/projb/databases")]
	[InlineData("/ui/wsa/projb/tasks")]
	[InlineData("/ui/wsa/projb/sessions")]
	[InlineData("/ui/wsa/projb/traces")]
	public async Task Member_of_wsa_cannot_reach_a_wsb_project_through_a_wsa_url(string url)
	{
		var auth = await LoginAsync("eve-iso");
		using var resp = await GetAsync(url, auth);
		resp.StatusCode.Should().Be(HttpStatusCode.NotFound,
			"a project that lives in ANOTHER workspace than the route's must not resolve, even though " +
			"the route workspace (wsa) is one eve may otherwise view");
	}

	// Same field-IDOR guard on the two WorkspaceAdmin-gated pages (Llm/Index, Config/Index) — a
	// same-class hole found and closed in this follow-up (same-class-cross-tenant-field-id-4c0359):
	// Llm/Index resolved its project by KEY ALONE (db.Projects.AnyAsync(p => p.Key == ProjectKey)),
	// so an admin of wsA could read/replace another tenant's LLM provider registry — INCLUDING its
	// api keys — via /ui/wsA/{project-of-wsB}/llm. Uses sysadmin so the WorkspaceAdmin POLICY itself
	// (which checks the ROUTE workspace only) always passes, isolating the assertion to the
	// project-binding filter: sysadmin's workspace free-pass must NOT extend to a project that does
	// not belong to the route workspace.
	[Theory]
	[InlineData("/ui/wsa/projb/config")]
	[InlineData("/ui/wsa/projb/llm")]
	public async Task Sysadmin_cannot_reach_a_wsb_project_through_a_wsa_url_on_admin_gated_pages(string url)
	{
		var auth = await LoginAsync("admin");
		using var resp = await GetAsync(url, auth);
		resp.StatusCode.Should().Be(HttpStatusCode.NotFound,
			"a project that lives in ANOTHER workspace than the route's must not resolve, even for sysadmin");
	}

	[Fact]
	public async Task Sysadmin_page_is_sysadmin_only()
	{
		var nomad = await LoginAsync("nomad");
		using var denied = await GetAsync("/ui/admin/sys", nomad);
		ShouldBeDenied(denied, "/ui/admin/sys counts workspaces, projects and users server-wide");

		var eve = await LoginAsync("eve-iso");
		using var deniedMember = await GetAsync("/ui/admin/sys", eve);
		ShouldBeDenied(deniedMember, "a workspace member is not a sysadmin");

		var admin = await LoginAsync("admin");
		using var ok = await GetAsync("/ui/admin/sys", admin);
		ok.StatusCode.Should().Be(HttpStatusCode.OK, "the sysadmin must keep the page");
	}

	// The invite path: a membership granted AFTER sign-in must take effect on the next request,
	// with the SAME cookie — claims are rebuilt from the DB per request (WorkspaceClaimsRefresher),
	// not frozen at login.
	[Fact]
	public async Task Membership_granted_after_sign_in_takes_effect_without_a_re_login()
	{
		var auth = await LoginAsync("latecomer");

		using (var before = await GetAsync("/ui/wsa", auth))
			ShouldBeDenied(before, "no membership yet");

		using (var scope = _fx.Factory.Services.CreateScope())
		{
			using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();
			var userId = db.Users.First(u => u.Username == "latecomer").Id;
			await db.InsertAsync(new WorkspaceMember { UserId = userId, WorkspaceKey = "wsa", Role = WorkspaceRole.Member });
		}

		using var after = await GetAsync("/ui/wsa", auth);
		after.StatusCode.Should().Be(HttpStatusCode.OK,
			"the membership is a DB fact — the same session must see it without signing in again");
	}

	[Fact]
	public async Task Landing_page_shows_the_empty_state_instead_of_a_redirect_loop()
	{
		var auth = await LoginAsync("nomad");
		using var resp = await GetAsync("/", auth);

		resp.StatusCode.Should().Be(HttpStatusCode.OK,
			"a user with no workspace has nowhere to be redirected to — the old code bounced them into $system");
		var html = await resp.Content.ReadAsStringAsync();
		html.Should().Contain("no-workspaces-card");
	}

	[Fact]
	public async Task AccessDenied_page_answers_403_and_is_not_the_login_form()
	{
		var auth = await LoginAsync("nomad");
		using var resp = await GetAsync("/AccessDenied?ReturnUrl=%2Fui%2Fwsa", auth);

		resp.StatusCode.Should().Be(HttpStatusCode.Forbidden, "the page IS the 403");
		var html = await resp.Content.ReadAsStringAsync();
		html.Should().Contain("access-denied-card");
		html.Should().NotContain("id=\"login-form\"", "it must not be the sign-in form");
	}

	// viewer-member-consistency: a Viewer can READ a board/log page (the class-wide policy is
	// WorkspaceViewer now), but the page's own MUTATION handlers still require Member+ — a Viewer
	// posting to them must be denied exactly like a non-member, and a Member must still succeed.
	async Task<HttpResponseMessage> PostAsync(string url, string authCookie, IDictionary<string, string> fields)
	{
		var getReq = new HttpRequestMessage(HttpMethod.Get, url);
		getReq.Headers.Add("Cookie", authCookie);
		using var getResp = await _client.SendAsync(getReq);
		var (token, afCookie) = ExtractAntiforgery(getResp, await getResp.Content.ReadAsStringAsync());

		fields["__RequestVerificationToken"] = token;
		var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(fields) };
		req.Headers.Add("Cookie", $"{authCookie}; {afCookie}");
		return await _client.SendAsync(req);
	}

	[Fact]
	public async Task Viewer_of_wsa_can_read_the_board_but_cannot_quick_add()
	{
		var auth = await LoginAsync("viewer-iso");

		using (var read = await GetAsync("/ui/wsa/proja/tasks/board1", auth))
			read.StatusCode.Should().Be(HttpStatusCode.OK, "a Viewer must be able to READ a board in their own workspace");

		using var post = await PostAsync("/ui/wsa/proja/tasks/board1?handler=Create", auth,
			new Dictionary<string, string> { ["name"] = "should not be created", ["priority"] = "100" });
		ShouldBeDenied(post, "quick-add is a MUTATION — a Viewer must not be able to create a task");
	}

	[Fact]
	public async Task Member_of_wsa_can_quick_add_to_the_board()
	{
		var auth = await LoginAsync("eve-iso");

		using var post = await PostAsync("/ui/wsa/proja/tasks/board1?handler=Create", auth,
			new Dictionary<string, string> { ["name"] = "member-created-task", ["priority"] = "100" });
		post.StatusCode.Should().Be(HttpStatusCode.Redirect, "a Member's quick-add must succeed (PRG back to the board)");
		post.Headers.Location!.ToString().Should().NotContain("/AccessDenied",
			"a Member is not denied — this is the mutation Member+ exists to allow");
	}

	[Fact]
	public async Task Viewer_of_wsa_cannot_save_a_query_but_can_read_the_logs_page()
	{
		var auth = await LoginAsync("viewer-iso");

		using (var read = await GetAsync("/ui/wsa/proja/logs", auth))
			read.StatusCode.Should().Be(HttpStatusCode.OK, "a Viewer must be able to READ the logs page");

		using var post = await PostAsync("/ui/wsa/proja/logs?handler=Save", auth,
			new Dictionary<string, string> { ["name"] = "should-not-save", ["kql"] = "events" });
		ShouldBeDenied(post, "saving a query is a MUTATION — a Viewer must not be able to create one");
	}

	[Fact]
	public async Task Member_of_wsa_can_save_a_query()
	{
		var auth = await LoginAsync("eve-iso");

		using var post = await PostAsync("/ui/wsa/proja/logs?handler=Save", auth,
			new Dictionary<string, string> { ["name"] = "member-saved-query", ["kql"] = "events" });
		post.StatusCode.Should().Be(HttpStatusCode.Redirect, "a Member's save must succeed (PRG back to the logs page)");
		post.Headers.Location!.ToString().Should().NotContain("/AccessDenied",
			"a Member is not denied — this is the mutation Member+ exists to allow");
	}

	[Fact]
	public async Task An_authenticated_user_opening_Login_is_sent_home()
	{
		var auth = await LoginAsync("nomad");
		using var resp = await GetAsync("/Login", auth);

		resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
		resp.Headers.Location!.ToString().Should().Be("/");
	}
}
