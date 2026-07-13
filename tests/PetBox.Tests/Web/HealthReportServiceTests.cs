using LinqToDB;
using LinqToDB.Async;
using PetBox.Core.Data;
using PetBox.Core.Health;

namespace PetBox.Tests.Web;

// IHealthReportService is the write door to the append-only HealthReports table, extracted so the
// POST /api/health handler stops opening core.db itself (AGENTS.md — the database is visible only in
// the service layer). The endpoint's authz is HTTP-level and belongs to the endpoint; what belongs
// to the SERVICE is the shape of the row it appends, which is what these tests pin.
public sealed class HealthReportServiceTests : IDisposable
{
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly HealthReportService _svc;

	public HealthReportServiceTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-health-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_svc = new HealthReportService(_db.Factory());
	}

	public void Dispose()
	{
		_db.Dispose();
		try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
	}

	static HealthReportInput Input(string svc = "api", string status = "ok", string project = "proj") =>
		new(svc, "API", new Dictionary<string, string> { ["project"] = project }, "1.0", "abc123", "2026-07-13", status);

	[Fact]
	public async Task RecordPush_appends_the_report_marked_as_a_push()
	{
		await _svc.RecordPushAsync(Input());

		var row = await _db.HealthReports.AsQueryable().SingleAsync();
		row.Svc.Should().Be("api");
		row.Name.Should().Be("API");
		row.Status.Should().Be("ok");
		row.Version.Should().Be("1.0");
		row.Sha.Should().Be("abc123");
		row.BuildDate.Should().Be("2026-07-13");
		row.Source.Should().Be("push", "the pull side (HealthPoller) is the only other writer, and the status page tells them apart");
		row.Tags.Should().Contain("project:proj");
		row.ReceivedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
	}

	[Fact]
	public async Task RecordPush_trims_the_svc_and_status_it_was_handed()
	{
		await _svc.RecordPushAsync(Input(svc: "  api  ", status: "  degraded  "));

		var row = await _db.HealthReports.AsQueryable().SingleAsync();
		row.Svc.Should().Be("api");
		row.Status.Should().Be("degraded");
	}

	// The table is a LOG, not a current-state row: retention sweeps it and the status page shows the
	// latest per (Svc, Tags). A second report from the same service must therefore APPEND.
	[Fact]
	public async Task RecordPush_appends_rather_than_overwriting_the_previous_report()
	{
		await _svc.RecordPushAsync(Input(status: "ok"));
		await _svc.RecordPushAsync(Input(status: "down"));

		var rows = await _db.HealthReports.AsQueryable().OrderBy(r => r.Id).ToListAsync();
		rows.Should().HaveCount(2);
		rows[0].Status.Should().Be("ok");
		rows[1].Status.Should().Be("down");
	}
}
