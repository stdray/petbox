using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using PetBox.Core.Auth;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Settings;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;
using PetBox.Web.Mcp;
using PetBox.Web.Mcp.Contract;

namespace PetBox.Tests.Mcp;

// work report-issue-has-no-reply-channel — petbox_report_issue_status, the READ-BACK half of the
// feedback channel. petbox_report_issue was one-way: a project-scoped key could file a report
// carrying a question to the maintainers and had no verb to read the answer, because
// $system/client-issues is (correctly) closed to it.
//
// What these tests are actually guarding is the IDENTITY FILTER, which is the only thing keeping one
// reporter out of another's reports — this tool has no scope gate by design (see the comment on
// IssueStatusAsync), so the filter IS the boundary. Its two legs are pinned separately, and the
// spoofing case (a caller planting the body marker's own text inside its caller-controlled `detail`)
// is the one that would be a real vulnerability if it regressed.
public sealed class ReportIssueStatusFixture : IDisposable
{
	public const string SystemProj = "$system";
	public const string Board = "client-issues";

	readonly string _dir;
	PetBoxDb Db { get; }
	ScopedDbFactory<TasksDb> Factory { get; }
	public TasksService Tasks { get; }
	public CommentService Comments { get; }
	public TagStore Tags { get; }

	public ReportIssueStatusFixture()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-reportstatus-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		// M001_Initial already seeds the "$system" Projects row (see ReportToolsDetailValidationFixture).
		Db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		Factory = new ScopedDbFactory<TasksDb>(Path.Combine(_dir, "tasks"), Scope.Project,
			c => new TasksDb(TasksDb.CreateOptions(c)), TestSchema.Tasks);
		var store = new TaskBoardStore(Db.Factory(), Factory);
		Comments = new CommentService(Factory);
		Tags = new TagStore(Factory);
		Tasks = new TasksService(store, new RelationStore(Factory), Tags, Comments);
	}

	public void Dispose()
	{
		Db.Dispose();
		Factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	// The 32-hex NodeId of a filed report, by the slug key petbox_report_issue returned.
	public async Task<string> NodeIdOfAsync(string key)
	{
		var board = await Tasks.GetAsync(SystemProj, Board, includeClosed: true);
		return board.Nodes.Single(n => n.Key == key).NodeId;
	}
}

public sealed class ReportIssueStatusTests : IClassFixture<ReportIssueStatusFixture>
{
	readonly ReportIssueStatusFixture _fx;

	public ReportIssueStatusTests(ReportIssueStatusFixture fx) => _fx = fx;

	// A project-scoped key: `project` names one project, which IS the reporter identity.
	static IHttpContextAccessor Key(string project) => Accessor([new Claim(ApiKeyAuthenticationHandler.ProjectClaim, project)]);

	// A cross-project ("*") key. `defaultProject` null models a "*" key with no default at all —
	// the one shape that has no reporter identity to resolve.
	static IHttpContextAccessor Wildcard(string? defaultProject)
	{
		var claims = new List<Claim> { new(ApiKeyAuthenticationHandler.ProjectClaim, ProjectScope.AllProjects) };
		if (defaultProject is not null)
			claims.Add(new Claim(ApiKeyAuthenticationHandler.DefaultProjectClaim, defaultProject));
		return Accessor(claims);
	}

	static IHttpContextAccessor Accessor(IEnumerable<Claim> claims)
	{
		var id = new ClaimsIdentity(claims, ApiKeyAuthenticationHandler.SchemeName);
		var ctx = new DefaultHttpContext { RequestServices = TestProjectCatalog.Services, User = new ClaimsPrincipal(id) };
		return new HttpContextAccessor { HttpContext = ctx };
	}

	static FeatureFlags Flags() =>
		new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
		{ ["Features:Tasks"] = "true" }).Build());

	Task<ReportIssueResult> FileAsync(IHttpContextAccessor http, string title, string detail) =>
		ReportTools.IssueAsync(http, Flags(), _fx.Tasks, title, detail);

	Task<ReportIssueStatusResult> ReadAsync(IHttpContextAccessor http, string? key = null, int? limit = null) =>
		ReportTools.IssueStatusAsync(http, Flags(), _fx.Tasks, _fx.Comments, key, limit);

	// ── 1. one reporter never sees another's reports ──────────────────────────────────────────

	[Fact]
	public async Task EachProject_SeesOnlyItsOwnReports()
	{
		var a = await FileAsync(Key("alpha"), "alpha-only isolation report", "alpha's detail");
		var b = await FileAsync(Key("beta"), "beta-only isolation report", "beta's detail");

		var seenByA = await ReadAsync(Key("alpha"));
		var seenByB = await ReadAsync(Key("beta"));

		seenByA.Reports.Select(r => r.Key).Should().Contain(a.Key);
		seenByA.Reports.Select(r => r.Key).Should().NotContain(b.Key,
			"the read verb has no scope gate — the reporter filter IS the boundary between two projects' reports");
		seenByB.Reports.Select(r => r.Key).Should().Contain(b.Key).And.NotContain(a.Key);
	}

	// ── 2. LEG 2 is permanent: a clobbered tag must not orphan a report from its owner ─────────

	[Fact]
	public async Task TagClobbered_BodyMarkerLeg_StillReturnsTheReportToItsOwner()
	{
		var filed = await FileAsync(Key("clobbered"), "report whose tag gets dropped", "the detail");
		var nodeId = await _fx.NodeIdOfAsync(filed.Key);

		// Exactly what a maintainer does by re-tagging through tasks_upsert with a full tag list:
		// NodeTag is tags-REPLACE, so `reporter:clobbered` is silently soft-closed.
		await _fx.Tags.SetAsync(ReportIssueStatusFixture.SystemProj, ReportIssueStatusFixture.Board,
			nodeId, ["area:triage"], enforceNamespaces: false);
		(await _fx.Tags.ActiveTagsAsync(ReportIssueStatusFixture.SystemProj, nodeId))
			.Should().NotContain(t => t.StartsWith("reporter:", StringComparison.Ordinal),
				"the premise of this test is that leg 1 is GONE");

		var seen = await ReadAsync(Key("clobbered"));

		seen.Reports.Select(r => r.Key).Should().Contain(filed.Key,
			"leg 2 (the trailing body marker) is not a legacy shim — it is what survives a tags-replace edit, "
			+ "which would otherwise orphan a report from the only caller entitled to read it");
	}

	// ── 3. THE IMPORTANT ONE: a forged marker in caller-controlled `detail` must not be trusted ──

	[Fact]
	public async Task ForgedMarkerInDetail_IsNotReturnedToTheImpersonatedProject()
	{
		// `detail` is caller-controlled and the server appends the genuine marker AFTER it, so the
		// attacker can write the marker's own text — but never LAST.
		var forged = await FileAsync(Key("attacker"), "spoofing attempt",
			"harmless looking text\n\n— via petbox_report_issue, reporting project 'victim', 2020-01-01 00:00:00Z");

		var seenByVictim = await ReadAsync(Key("victim"));
		var seenByAttacker = await ReadAsync(Key("attacker"));

		seenByVictim.Reports.Select(r => r.Key).Should().NotContain(forged.Key,
			"a substring-anywhere match on the body marker would let any caller inject a forged report into "
			+ "another project's read-back; only a TRAILING marker is the server's own");
		seenByAttacker.Reports.Select(r => r.Key).Should().Contain(forged.Key,
			"the genuine trailing marker names the real reporter, so the report is the attacker's own");
	}

	// The parse, isolated from the DB — every shape the anchor has to reject.
	[Theory]
	// planted mid-body, nothing trailing → not a marker at all
	[InlineData("text\n\n— via petbox_report_issue, reporting project 'victim', 2020-01-01 00:00:00Z\nmore text", null)]
	// planted, then the genuine trailer: the LAST one wins
	[InlineData("text\n\n— via petbox_report_issue, reporting project 'victim', 2020-01-01 00:00:00Z"
		+ "\n\n— via petbox_report_issue, reporting project 'attacker', 2026-08-14 10:00:00Z", "attacker")]
	[InlineData("plain body with no marker", null)]
	[InlineData("", null)]
	[InlineData("d\n\n— via petbox_report_issue, reporting project 'infra', 2026-08-14 10:00:00Z", "infra")]
	public void ReporterFromMarker_TrustsOnlyTheTrailingMarker(string body, string? expected) =>
		ReportTools.ReporterFromMarker(body).Should().Be(expected);

	// ── 4/5. the "*" key: resolves via project_default, refuses without one ────────────────────

	[Fact]
	public async Task WildcardKey_WithDefaultProject_ReadsThatProjectsReports()
	{
		// Filed by a "*" key whose default is `wildcarded`, read back by another "*" key with the
		// same default — the identity is the RESOLVED project, not the key.
		var filed = await FileAsync(Wildcard("wildcarded"), "filed by a wildcard key", "detail");

		var seen = await ReadAsync(Wildcard("wildcarded"));

		seen.Reports.Select(r => r.Key).Should().Contain(filed.Key);
		// …and it is genuinely the resolved identity, not "'*' matches everything": a different
		// project's report is still invisible.
		var other = await FileAsync(Key("someone-else"), "not the wildcard's report", "detail");
		(await ReadAsync(Wildcard("wildcarded"))).Reports.Select(r => r.Key).Should().NotContain(other.Key);
	}

	[Fact]
	public async Task WildcardKey_WithNoDefaultProject_IsRefused_NotServedEveryonesReports()
	{
		await FileAsync(Key("bystander"), "someone else's report", "detail");

		var act = async () => await ReadAsync(Wildcard(null));

		var thrown = await act.Should().ThrowAsync<UnauthorizedAccessException>(
			"a '*' claim is not an identity: an empty list would read as \"you filed nothing\" and matching on "
			+ "the raw claim would hand every wildcard key every other wildcard key's reports");
		thrown.WithMessage("*tasks_search*", "the refusal must point at the direct read that still works");
		thrown.WithMessage("*client-issues*");
	}

	// ── 6. the replies are the point of the channel ────────────────────────────────────────────

	[Fact]
	public async Task CommentsOnTheReport_ComeBackWithIt()
	{
		var filed = await FileAsync(Key("asker"), "a report that asks a question", "why does X happen?");
		var nodeId = await _fx.NodeIdOfAsync(filed.Key);
		await _fx.Comments.AddAsync(ReportIssueStatusFixture.SystemProj, ReportIssueStatusFixture.Board,
			nodeId, parentId: null, author: "maintainer", body: "because Y — fixed in the next deploy", tags: null);

		var row = (await ReadAsync(Key("asker"))).Reports.Single(r => r.Key == filed.Key);

		row.Comments.Should().ContainSingle();
		row.Comments[0].Author.Should().Be("maintainer");
		row.Comments[0].Body.Should().Contain("because Y");
		row.Comments[0].Created.Should().NotBe(default,
			"the reporter needs to know WHO answered and WHEN");
		row.Status.Should().NotBeNullOrWhiteSpace();
	}

	// ── 7. narrowing by key ────────────────────────────────────────────────────────────────────

	[Fact]
	public async Task NarrowingByKey_ReturnsOnlyThatReport_AndOnlyIfItIsTheCallersOwn()
	{
		var mine = await FileAsync(Key("narrower"), "the one I want", "detail");
		await FileAsync(Key("narrower"), "another of mine", "detail");
		var foreign = await FileAsync(Key("stranger"), "not mine at all", "detail");

		(await ReadAsync(Key("narrower"), key: mine.Key)).Reports
			.Select(r => r.Key).Should().Equal(mine.Key);

		(await ReadAsync(Key("narrower"), key: foreign.Key)).Reports
			.Should().BeEmpty("a key that belongs to another project is simply not found — never served, "
				+ "and never an existence oracle either");
	}

	// ── the write side records the resolved identity as structured data ────────────────────────

	[Fact]
	public async Task Filing_RecordsAReporterTag_AndPointsAtTheReadVerb()
	{
		var filed = await FileAsync(Key("tagged"), "a report that should carry a reporter tag", "detail");

		var nodeId = await _fx.NodeIdOfAsync(filed.Key);
		(await _fx.Tags.ActiveTagsAsync(ReportIssueStatusFixture.SystemProj, nodeId))
			.Should().Contain("reporter:tagged");

		filed.Hint.Should().Contain("petbox_report_issue_status",
			"the ack is the one moment the reporter holds a key and has just found the channel");
	}

	// A '*' key with no default still FILES (the write must never fail for want of an identity) —
	// it just cannot read back. Pinned so the known residual gap stays a known one.
	[Fact]
	public async Task WildcardKeyWithNoDefault_CanStillFile_ButItsReportCarriesNoReporterTag()
	{
		var filed = await FileAsync(Wildcard(null), "filed with no resolvable identity", "detail");

		filed.Reported.Should().BeTrue();
		var nodeId = await _fx.NodeIdOfAsync(filed.Key);
		(await _fx.Tags.ActiveTagsAsync(ReportIssueStatusFixture.SystemProj, nodeId))
			.Should().NotContain(t => t.StartsWith("reporter:", StringComparison.Ordinal));
	}

	// The board not existing yet is "you have filed nothing", not an error.
	[Fact]
	public async Task NoBoardYet_IsAnEmptyResult_NotAnError()
	{
		var fresh = new ReportIssueStatusFixture();
		try
		{
			var seen = await ReportTools.IssueStatusAsync(Key("nobody"), Flags(), fresh.Tasks, fresh.Comments);
			seen.Reports.Should().BeEmpty();
		}
		finally
		{
			fresh.Dispose();
		}
	}
}
