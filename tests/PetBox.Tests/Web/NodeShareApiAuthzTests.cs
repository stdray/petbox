using System.Net;
using System.Net.Http.Json;
using LinqToDB;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Log.Core.Contract;
using PetBox.Tasks.Contract;
using PetBox.Web.Mcp;
using PetBox.Web.Tasks;

namespace PetBox.Tests.Web;

// The SERVER half of node sharing (work `node-share-backend`, specs `node-share`,
// `node-share-scope`, `node-share-lifetime`, `share-link-revocable`), driven over the real HTTP
// surface with X-Api-Key — the ShareApiAuthzTests idiom, in its OWN file because it is its own
// feature and shares only the revoke route.
//
// This file exists to answer four questions the unit-level directory tests cannot:
//   * does the tenant declaration on POST /api/share/node actually get READ (a key for project A
//     must not publish project B's node);
//   * does the scope/commentId validation hold in BOTH directions, including the half no PEP can
//     see — a comment that belongs to a DIFFERENT node;
//   * does the ONE revoke surface (REST and MCP) reach a node link without the caller saying which
//     kind of token they hold, while the log family's behaviour is unchanged;
//   * does a link with NO expiry survive the retention sweep that deletes expired ones.
public sealed class NodeShareApiAuthzFixture : IAsyncLifetime
{
	public const string Board = "work";
	public const string LogName = "petbox";

	public const string ProjA = "nodeshareproja";
	public const string ProjB = "nodeshareprojb";
	public const string KeyA = "yb_key_nodeshare_a";
	public const string KeyB = "yb_key_nodeshare_b";

	public WebApplicationFactory<Program> Factory { get; }
	public HttpClient Client { get; private set; } = null!;

	// A real node with a real comment under it, in ProjA — the scope=comment path is about the
	// relation between the two, so both have to exist for the assertions to mean anything.
	public string NodeId { get; private set; } = "";
	public string CommentId { get; private set; } = "";

	// A SECOND node in the same project, with its own comment. This pair is the trap: publishing
	// OtherComment under NodeId is a request the tenant check waves through (one caller, one
	// project, both objects theirs) and only the belongs-to check can refuse.
	public string OtherNodeId { get; private set; } = "";
	public string OtherCommentId { get; private set; } = "";

	public NodeShareApiAuthzFixture()
	{
		Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");
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
						// BOTH pinned: Tasks maps the mint endpoint, Logging maps the revoke route
						// the mint endpoint deliberately does NOT duplicate.
						["Features:Tasks"] = "true",
						["Features:Logging"] = "true",
					});
				});
			});
	}

	public async ValueTask InitializeAsync()
	{
		var cs = Factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("PetBox")!;
		TestSchema.Core(cs);
		Client = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

		using var scope = Factory.Services.CreateScope();
		using (var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open())
		{
			await db.InsertAsync(new Project { Key = ProjA, WorkspaceKey = "$system", Name = "ProjA" });
			await db.InsertAsync(new Project { Key = ProjB, WorkspaceKey = "$system", Name = "ProjB" });
			await db.InsertAsync(new ApiKey { Key = KeyA, ProjectKey = ProjA, Scopes = "tasks:write", CreatedAt = DateTime.UtcNow });
			await db.InsertAsync(new ApiKey { Key = KeyB, ProjectKey = ProjB, Scopes = "tasks:write", CreatedAt = DateTime.UtcNow });
		}

		// A named log in ProjA, so the MCP test below can mint a REAL log share link and prove that
		// family still revokes through the same verb.
		await scope.ServiceProvider.GetRequiredService<ILogStore>().CreateAsync(ProjA, LogName, "node-share revoke coverage");

		var tasks = scope.ServiceProvider.GetRequiredService<ITasksService>();
		var comments = scope.ServiceProvider.GetRequiredService<ICommentService>();

		await tasks.UpsertAsync(ProjA, Board,
		[
			new NodePatch { Key = "shared-node", Title = "Shared", Body = "the published body" },
			new NodePatch { Key = "other-node", Title = "Other", Body = "not published" },
		]);

		NodeId = (await tasks.GetNodeBySlugAsync(ProjA, Board, "shared-node"))!.Node.NodeId;
		OtherNodeId = (await tasks.GetNodeBySlugAsync(ProjA, Board, "other-node"))!.Node.NodeId;

		CommentId = (await comments.AddAsync(ProjA, Board, NodeId, null, "tester", "a remark", null)).Id!;
		OtherCommentId = (await comments.AddAsync(ProjA, Board, OtherNodeId, null, "tester", "elsewhere", null)).Id!;
	}

	public async ValueTask DisposeAsync()
	{
		Client.Dispose();
		await Factory.DisposeAsync();
	}
}

public sealed class NodeShareApiAuthzTests : IClassFixture<NodeShareApiAuthzFixture>
{
	readonly NodeShareApiAuthzFixture _fx;
	readonly HttpClient _client;

	public NodeShareApiAuthzTests(NodeShareApiAuthzFixture fx)
	{
		_fx = fx;
		_client = fx.Client;
	}

	static HttpRequestMessage MintReq(
		string apiKey, string projectKey, string nodeId, string scope = "body",
		string? commentId = null, int? ttlMinutes = null, string board = NodeShareApiAuthzFixture.Board)
	{
		var req = new HttpRequestMessage(HttpMethod.Post, "/api/share/node");
		req.Headers.Add("X-Api-Key", apiKey);
		req.Content = JsonContent.Create(new { projectKey, board, nodeId, scope, commentId, ttlMinutes });
		return req;
	}

	static HttpRequestMessage RevokeReq(string apiKey, string token, string projectKey)
	{
		var req = new HttpRequestMessage(HttpMethod.Delete, $"/api/share/{token}");
		req.Headers.Add("X-Api-Key", apiKey);
		req.Content = JsonContent.Create(new { projectKey });
		return req;
	}

	async Task<NodeShareCreatedResponse> MintAsync(
		string scope = "body", string? commentId = null, int? ttlMinutes = null, string? nodeId = null)
	{
		using var resp = await _client.SendAsync(MintReq(
			NodeShareApiAuthzFixture.KeyA, NodeShareApiAuthzFixture.ProjA,
			nodeId ?? _fx.NodeId, scope, commentId, ttlMinutes));
		resp.StatusCode.Should().Be(HttpStatusCode.OK, "minting must succeed in test setup");
		return (await resp.Content.ReadFromJsonAsync<NodeShareCreatedResponse>())!;
	}

	static PetBoxDb OpenCore(IServiceScope scope) =>
		scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();

	// ── MINT: THE TENANT AXIS ────────────────────────────────────────────────────────────────────

	[Fact]
	public async Task Mint_OwnProject_Succeeds_AndStoresWhatTheLinkPublishes()
	{
		var minted = await MintAsync();

		minted.Id.Should().HaveLength(40, "the token is 20 random bytes, hex — the same mint as a log link");

		using var scope = _fx.Factory.Services.CreateScope();
		using var db = OpenCore(scope);
		var row = db.NodeShares.FirstOrDefault(s => s.Id == minted.Id);
		row.Should().NotBeNull();
		row!.ProjectKey.Should().Be(NodeShareApiAuthzFixture.ProjA);
		row.Board.Should().Be(NodeShareApiAuthzFixture.Board);
		row.NodeId.Should().Be(_fx.NodeId);
		row.Scope.Should().Be("body");
		row.CommentId.Should().BeNull();
	}

	// The sharpest question on this endpoint: `projectKey` is a BODY field, fully caller-controlled,
	// and the only thing that reads it is the [TenantFrom(BodyField, "projectKey")] declaration. If
	// that declaration were missing the call would look identical from the outside — a 200 with a
	// token — while publishing another tenant's prose to the anonymous internet.
	[Fact]
	public async Task Mint_ForeignProject_Returns403_AndCreatesNoRow()
	{
		using var resp = await _client.SendAsync(MintReq(
			NodeShareApiAuthzFixture.KeyA, NodeShareApiAuthzFixture.ProjB, _fx.NodeId));

		resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
			"a key authorized only for project A must not publish a node under project B");

		using var scope = _fx.Factory.Services.CreateScope();
		using var db = OpenCore(scope);
		db.NodeShares.Any(s => s.ProjectKey == NodeShareApiAuthzFixture.ProjB).Should().BeFalse(
			"no token must have been minted for the foreign project");
	}

	// ── MINT: SCOPE × COMMENTID ──────────────────────────────────────────────────────────────────

	[Fact]
	public async Task Mint_ScopeComment_WithoutCommentId_Returns400()
	{
		using var resp = await _client.SendAsync(MintReq(
			NodeShareApiAuthzFixture.KeyA, NodeShareApiAuthzFixture.ProjA, _fx.NodeId, scope: "comment"));

		resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
			"scope=comment names one comment — without an id the link would publish nothing");
	}

	// The half no authorization check can catch. Both objects belong to the caller's own project, so
	// the tenant PEP is satisfied; what is wrong is the RELATION — the comment hangs under a
	// different node. A link like that renders one node's identity above another node's discussion,
	// and the reader (who holds only a token) cannot tell.
	[Fact]
	public async Task Mint_ScopeComment_WithACommentFromAnotherNode_Returns400_AndCreatesNoRow()
	{
		using var resp = await _client.SendAsync(MintReq(
			NodeShareApiAuthzFixture.KeyA, NodeShareApiAuthzFixture.ProjA, _fx.NodeId,
			scope: "comment", commentId: _fx.OtherCommentId));

		resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
			"the comment must belong to the node the link names — same project is not enough");

		using var scope = _fx.Factory.Services.CreateScope();
		using var db = OpenCore(scope);
		db.NodeShares.Any(s => s.CommentId == _fx.OtherCommentId).Should().BeFalse();
	}

	[Fact]
	public async Task Mint_ScopeComment_WithItsOwnNodesComment_Succeeds_AndStoresTheCommentId()
	{
		var minted = await MintAsync(scope: "comment", commentId: _fx.CommentId);

		using var scope = _fx.Factory.Services.CreateScope();
		using var db = OpenCore(scope);
		var row = db.NodeShares.First(s => s.Id == minted.Id);
		row.Scope.Should().Be("comment");
		row.CommentId.Should().Be(_fx.CommentId);
	}

	// The other direction, which is easy to forget and just as wrong: a stored commentId the reader
	// will not honour is a grant whose recorded extent and rendered extent disagree.
	[Theory]
	[InlineData("body")]
	[InlineData("full")]
	public async Task Mint_ScopeBodyOrFull_WithACommentId_Returns400(string scope)
	{
		using var resp = await _client.SendAsync(MintReq(
			NodeShareApiAuthzFixture.KeyA, NodeShareApiAuthzFixture.ProjA, _fx.NodeId,
			scope: scope, commentId: _fx.CommentId));

		resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
			$"scope={scope} publishes no single comment, so a commentId has no meaning in it");
	}

	[Fact]
	public async Task Mint_UnknownScope_Returns400()
	{
		using var resp = await _client.SendAsync(MintReq(
			NodeShareApiAuthzFixture.KeyA, NodeShareApiAuthzFixture.ProjA, _fx.NodeId, scope: "everything"));

		resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
			"the vocabulary is closed — an unknown scope must not be stored and interpreted later");
	}

	// ── MINT: LIFETIME (spec node-share-lifetime) ────────────────────────────────────────────────

	// RETARGETED by work node-share-public-page. This test was written as "the stored row has a NULL
	// ExpiresAt" because on the day it was written there was no public reader to ask — the row's
	// state was the only observable. There is one now (page:/ShareNode), so the claim is measured
	// where it actually matters: a link minted with no TTL SERVES ITS CONTENT. The row assertion is
	// kept as the mechanism control (null, not a far-future date), but it is no longer the evidence.
	//
	// The failure this guards is specific and silent: a reader that compared `ExpiresAt < UtcNow`
	// by hand would see default(DateTime) for a NULL and 404 every permanent link ever minted,
	// while every row-level test in this file stayed green. Hence NodeShare.IsExpiredAt.
	[Fact]
	public async Task Mint_WithoutTtl_NeverExpires_AndThePublicPageServesIt()
	{
		var minted = await MintAsync();

		minted.ExpiresAt.Should().BeNull(
			"the response must say 'no expiry' rather than encode it as some far-future date");

		using (var scope = _fx.Factory.Services.CreateScope())
		using (var db = OpenCore(scope))
			db.NodeShares.First(s => s.Id == minted.Id).ExpiresAt.Should().BeNull();

		// Anonymous on purpose: no cookie, no X-Api-Key — the token is the whole authorization.
		using var page = await _client.GetAsync($"/ui/share/node/{minted.Id}");

		page.StatusCode.Should().Be(HttpStatusCode.OK,
			"a link with no expiry is not expired — the public reader must serve it, and this is the "
			+ "assertion that would catch a null-unsafe expiry comparison that the row-state check cannot");
		(await page.Content.ReadAsStringAsync()).Should().Contain("the published body",
			"and serving it means the node's content, not an empty shell with a 200 on it");
	}

	[Fact]
	public async Task Mint_WithZeroTtl_IsTheSameAsOmittingIt()
	{
		var minted = await MintAsync(ttlMinutes: 0);

		minted.ExpiresAt.Should().BeNull("0 is the explicit spelling of 'never expires'");
	}

	[Fact]
	public async Task Mint_WithPositiveTtl_StoresAnExpiry()
	{
		var minted = await MintAsync(ttlMinutes: 30);

		minted.ExpiresAt.Should().NotBeNull().And.BeAfter(DateTime.UtcNow);
	}

	// A negative ttl is far likelier to be an arithmetic slip than a request for a permanent link,
	// and folding it into "forever" would grant the STRONGEST option on malformed input.
	[Fact]
	public async Task Mint_WithNegativeTtl_Returns400_RatherThanSilentlyMintingAPermanentLink()
	{
		using var resp = await _client.SendAsync(MintReq(
			NodeShareApiAuthzFixture.KeyA, NodeShareApiAuthzFixture.ProjA, _fx.NodeId, ttlMinutes: -5));

		resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
	}

	// ── REVOKE: ONE ROUTE, TWO TOKEN FAMILIES ────────────────────────────────────────────────────

	// RETARGETED by work node-share-public-page, same reason as the lifetime test above: "the row is
	// gone" was a proxy for "it stopped serving", chosen because no reader existed to ask. Now the
	// claim is made where the owner actually cares about it — the SAME token, served before revoke
	// and 404 after, with no request in between. A revoke that deleted the row but left a cached or
	// otherwise still-answering page would have passed the old assertion and failed the real one.
	[Fact]
	public async Task Revoke_NodeToken_ThroughTheExistingShareRoute_ImmediatelyStopsThePublicPage()
	{
		var minted = await MintAsync();

		// The control: it really was serving. Without this, a page that 404s for an unrelated reason
		// would let the revoke assertion below pass while proving nothing.
		using (var before = await _client.GetAsync($"/ui/share/node/{minted.Id}"))
			before.StatusCode.Should().Be(HttpStatusCode.OK, "the link serves before it is revoked");

		using var resp = await _client.SendAsync(RevokeReq(
			NodeShareApiAuthzFixture.KeyA, minted.Id, NodeShareApiAuthzFixture.ProjA));

		resp.StatusCode.Should().Be(HttpStatusCode.OK,
			"node sharing got no revoke route of its own — the existing one must find this token");

		using (var after = await _client.GetAsync($"/ui/share/node/{minted.Id}"))
			after.StatusCode.Should().Be(HttpStatusCode.NotFound,
				"revoke is a hard delete and the reader holds nothing of its own — the very next request "
				+ "must be refused. For a link with no TTL this is the ONLY way it ever stops serving");

		using var scope = _fx.Factory.Services.CreateScope();
		using var db = OpenCore(scope);
		db.NodeShares.Any(s => s.Id == minted.Id).Should().BeFalse(
			"the mechanism behind that 404 is a hard delete, not a flag the reader could forget to read");
	}

	[Fact]
	public async Task Revoke_NodeToken_ForeignProject_Returns404_AndTheRowSurvives()
	{
		var minted = await MintAsync();

		using var resp = await _client.SendAsync(RevokeReq(
			NodeShareApiAuthzFixture.KeyB, minted.Id, NodeShareApiAuthzFixture.ProjB));

		resp.StatusCode.Should().Be(HttpStatusCode.NotFound,
			"a caller honestly authorized for ITS OWN project must not revoke another's node link, and "
			+ "must not learn that it exists either");

		using var scope = _fx.Factory.Services.CreateScope();
		using var db = OpenCore(scope);
		db.NodeShares.Any(s => s.Id == minted.Id).Should().BeTrue();
	}

	[Fact]
	public async Task Revoke_NodeToken_Twice_SecondIs404_SameShapeAsAnUnknownToken()
	{
		var minted = await MintAsync();

		using (var first = await _client.SendAsync(RevokeReq(
			NodeShareApiAuthzFixture.KeyA, minted.Id, NodeShareApiAuthzFixture.ProjA)))
			first.StatusCode.Should().Be(HttpStatusCode.OK);

		using var second = await _client.SendAsync(RevokeReq(
			NodeShareApiAuthzFixture.KeyA, minted.Id, NodeShareApiAuthzFixture.ProjA));
		second.StatusCode.Should().Be(HttpStatusCode.NotFound);

		using var unknown = await _client.SendAsync(RevokeReq(
			NodeShareApiAuthzFixture.KeyA, "tok-never-existed-at-all", NodeShareApiAuthzFixture.ProjA));
		unknown.StatusCode.Should().Be(second.StatusCode,
			"already-revoked, foreign and never-existed are one indistinguishable answer");
	}

	// ── REVOKE ON THE MCP SURFACE ────────────────────────────────────────────────────────────────
	//
	// The same act, same verb, driven with the host's REAL service. The log half is asserted in the
	// SAME test on purpose: the requirement is not "node links can be revoked" but "one verb revokes
	// either kind", and a regression that broke the log path while fixing the node path would pass
	// two separate tests.
	[Fact]
	public async Task ShareRevokeTool_RevokesANodeLink_AndStillRevokesALogLink()
	{
		var node = await MintAsync();

		using var mintLog = new HttpRequestMessage(HttpMethod.Post, "/api/share");
		mintLog.Headers.Add("X-Api-Key", NodeShareApiAuthzFixture.KeyA);
		mintLog.Content = JsonContent.Create(new
		{
			projectKey = NodeShareApiAuthzFixture.ProjA,
			kql = "events | take 10",
			ttlMinutes = 60,
			logName = NodeShareApiAuthzFixture.LogName,
		});
		using var logResp = await _client.SendAsync(mintLog);
		logResp.StatusCode.Should().Be(HttpStatusCode.OK);
		var logToken = (await logResp.Content.ReadFromJsonAsync<ShareCreatedResponse>())!.Id;

		using var scope = _fx.Factory.Services.CreateScope();
		var revocation = scope.ServiceProvider.GetRequiredService<IShareRevocationService>();

		(await ShareTools.RevokeAsync(revocation, NodeShareApiAuthzFixture.ProjA, node.Id))
			.Revoked.Should().BeTrue();
		(await ShareTools.RevokeAsync(revocation, NodeShareApiAuthzFixture.ProjA, logToken))
			.Revoked.Should().BeTrue("the log family's behaviour must be unchanged by the second lookup");

		using var db = OpenCore(scope);
		db.NodeShares.Any(s => s.Id == node.Id).Should().BeFalse();
		db.ShareLinks.Any(s => s.Id == logToken).Should().BeFalse();

		using var tsv = await _client.GetAsync($"/api/share/{logToken}/tsv");
		tsv.StatusCode.Should().Be(HttpStatusCode.NotFound,
			"and the anonymous log reader must stop serving it, exactly as before");
	}

	[Fact]
	public async Task ShareRevokeTool_ForeignProjectsNodeToken_IsNotFound_AndTheTokenSurvives()
	{
		var node = await MintAsync();

		using var scope = _fx.Factory.Services.CreateScope();
		var revocation = scope.ServiceProvider.GetRequiredService<IShareRevocationService>();

		var act = async () => await ShareTools.RevokeAsync(revocation, NodeShareApiAuthzFixture.ProjB, node.Id);
		await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("share link not found");

		using var db = OpenCore(scope);
		db.NodeShares.Any(s => s.Id == node.Id).Should().BeTrue();
	}

	// ── RETENTION (spec node-share-lifetime) ─────────────────────────────────────────────────────
	//
	// The one job in the tree that DELETES share rows on its own. A never-expiring link is exactly
	// the row it must not touch, and "NULL is not less than now" is a property of SQL three-valued
	// logic rather than of anything written down — so it is measured, in the same pass that proves
	// the sweep is really running (an expired sibling disappears).
	[Fact]
	public async Task RetentionPass_DeletesAnExpiredNodeLink_ButNeverOneWithNoExpiry()
	{
		var forever = await MintAsync();
		var shortLived = await MintAsync(ttlMinutes: 5);

		using var scope = _fx.Factory.Services.CreateScope();
		var retention = _fx.Factory.Services.GetRequiredService<PetBox.Log.Core.Retention.RetentionService>();

		// A clock an hour ahead: the 5-minute link is expired at that instant, the other one has no
		// instant it is expired at.
		await retention.RunPassAsync(DateTime.UtcNow.AddHours(1), default);

		using var db = OpenCore(scope);
		db.NodeShares.Any(s => s.Id == shortLived.Id).Should().BeFalse(
			"the control: the sweep really ran, and an expired link is what it is for");
		db.NodeShares.Any(s => s.Id == forever.Id).Should().BeTrue(
			"a link with no expiry is not 'expired long ago' — retention must never pick it up");
	}
}
