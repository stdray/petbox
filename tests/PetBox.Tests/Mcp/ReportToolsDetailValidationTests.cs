using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Settings;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;
using PetBox.Web.Mcp;

namespace PetBox.Tests.Mcp;

// work/mcp-surface-naming-cleanup, wave 1, point 7 — petbox_report_issue.detail is declared
// `string detail` (no `?`, i.e. required by signature) and its [Description] promises a
// content-bearing report, but only `title` was ever checked for emptiness (ReportTools.cs).
// A blank/whitespace `detail` used to sail through into the report body silently. This pins the
// same non-empty check as `title` and confirms a real detail still lands.
public sealed class ReportToolsDetailValidationFixture : IDisposable
{
	public const string SystemProj = "$system";

	readonly string _dir;
	PetBoxDb Db { get; }
	ScopedDbFactory<TasksDb> Factory { get; }
	public TasksService Tasks { get; }

	public ReportToolsDetailValidationFixture()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-reporttools-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		// M001_Initial already seeds a Projects row for "$system" (the fixed maintainer-triage
		// tenant petbox_report_issue writes into) — inserting a second one here would violate
		// Projects.Key's UNIQUE constraint, so this fixture only builds the tasks-tier factory.
		Db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		Factory = new ScopedDbFactory<TasksDb>(Path.Combine(_dir, "tasks"), Scope.Project,
			c => new TasksDb(TasksDb.CreateOptions(c)), TestSchema.Tasks);
		var store = new TaskBoardStore(Db.Factory(), Factory);
		var comments = new CommentService(Factory);
		Tasks = new TasksService(store, new RelationStore(Factory), new TagStore(Factory), comments);
	}

	public void Dispose()
	{
		Db.Dispose();
		Factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}
}

public sealed class ReportToolsDetailValidationTests : IClassFixture<ReportToolsDetailValidationFixture>
{
	readonly ReportToolsDetailValidationFixture _fx;

	public ReportToolsDetailValidationTests(ReportToolsDetailValidationFixture fx) => _fx = fx;

	static IHttpContextAccessor Http()
	{
		var id = new ClaimsIdentity([new Claim("project", "caller-proj")], "test");
		var ctx = new DefaultHttpContext { RequestServices = TestProjectCatalog.Services, User = new ClaimsPrincipal(id) };
		return new HttpContextAccessor { HttpContext = ctx };
	}

	static FeatureFlags Flags() =>
		new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
		{ ["Features:Tasks"] = "true" }).Build());

	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	[InlineData("\t\n ")]
	public async Task IssueAsync_RejectsEmptyOrWhitespaceDetail(string detail)
	{
		var act = async () => await ReportTools.IssueAsync(Http(), Flags(), _fx.Tasks, "A real title", detail);

		(await act.Should().ThrowAsync<ArgumentException>()).WithMessage("*detail is required*",
			"empty title already refuses this way (ReportTools.cs); detail must refuse the same way, "
			+ "not sail silently into the report body");
	}

	[Fact]
	public async Task IssueAsync_AcceptsMeaningfulDetail()
	{
		var r = await ReportTools.IssueAsync(Http(), Flags(), _fx.Tasks, "A real bug",
			"Did X, expected Y, got Z. Repro: call foo() twice.");

		r.Reported.Should().BeTrue();
		r.Project.Should().Be(ReportToolsDetailValidationFixture.SystemProj);
	}
}
