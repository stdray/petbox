using System.Security.Claims;
using LinqToDB;
using Microsoft.AspNetCore.Http;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Web.Mcp;

namespace PetBox.Tests.Mcp;

// Exercises the health_search MCP tool directly (mocked HttpContext + a real core
// petbox.db). Covers latest-per-service selection, the stale computation, ISO time
// normalization on read, project isolation, and the opt-in history window.
public sealed class HealthToolsTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;

	public HealthToolsTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-healthtools-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
	}

	public void Dispose()
	{
		_db.Dispose();
		TestDirs.CleanupOrDefer(_dir);
	}

	// Append a report for (svc, project) received at `receivedAt`.
	void Push(string svc, string project, string status, DateTime receivedAt,
		string? version = null, string? sha = null, string source = "push")
	{
		var tags = new Dictionary<string, string>(StringComparer.Ordinal) { ["project"] = project };
		_db.Insert(new HealthReport
		{
			Svc = svc,
			Name = svc + "-name",
			Tags = HealthTags.Canonical(tags),
			Version = version,
			Sha = sha,
			Status = status,
			ReceivedAt = receivedAt,
			Source = source,
		});
	}

	[Fact]
	public async Task LatestPerService_ReturnsNewestReportOnly()
	{
		var now = DateTime.UtcNow;
		Push("api", Proj, "ok", now.AddSeconds(-120), version: "1.0.0");
		Push("api", Proj, "degraded", now.AddSeconds(-30), version: "1.0.1"); // newest for api
		Push("worker", Proj, "ok", now.AddSeconds(-10), version: "2.0.0");

		var http = Http("health:read");
		var res = await HealthTools.SearchAsync(http, _db, Proj);

		res.Services.Select(s => s.Svc).Should().Equal("api", "worker"); // svc-sorted, one row each
		var api = res.Services.Single(s => s.Svc == "api");
		api.Status.Should().Be("degraded"); // the newest, not the older "ok"
		api.Version.Should().Be("1.0.1");
		api.History.Should().BeNull(); // history off by default
	}

	[Fact]
	public async Task Stale_IsAgeVsThreshold()
	{
		var now = DateTime.UtcNow;
		Push("fresh", Proj, "ok", now.AddSeconds(-30));
		Push("old", Proj, "ok", now.AddSeconds(-600));

		var http = Http("health:read");
		var res = await HealthTools.SearchAsync(http, _db, Proj, staleThresholdSeconds: 300);

		res.Services.Single(s => s.Svc == "fresh").Stale.Should().BeFalse();
		res.Services.Single(s => s.Svc == "fresh").AgeSeconds.Should().BeInRange(25, 90);
		res.Services.Single(s => s.Svc == "old").Stale.Should().BeTrue();
	}

	[Fact]
	public async Task ReceivedAt_IsIsoUtcWithTAndZ()
	{
		Push("api", Proj, "ok", DateTime.UtcNow.AddSeconds(-5));

		var http = Http("health:read");
		var res = await HealthTools.SearchAsync(http, _db, Proj);

		var iso = res.Services.Single().ReceivedAt;
		iso.Should().Contain("T").And.EndWith("Z"); // ISO 'T' separator + UTC 'Z', not the stored space form
		DateTime.TryParse(iso, out _).Should().BeTrue();
	}

	[Fact]
	public async Task ProjectIsolation_ForeignProjectKey_Unauthorized()
	{
		Push("api", Proj, "ok", DateTime.UtcNow);

		// A key scoped to another project may not read this project.
		var other = Http("health:read", project: "other");
		await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
			HealthTools.SearchAsync(other, _db, Proj));
	}

	[Fact]
	public async Task ProjectIsolation_OnlyOwnProjectsServicesReturned()
	{
		var now = DateTime.UtcNow;
		Push("mine", Proj, "ok", now);
		Push("theirs", "other", "ok", now);

		var http = Http("health:read");
		var res = await HealthTools.SearchAsync(http, _db, Proj);
		res.Services.Select(s => s.Svc).Should().Equal("mine"); // "theirs" belongs to another project
	}

	[Fact]
	public async Task WildcardKey_ReadsAnyProject()
	{
		var now = DateTime.UtcNow;
		Push("theirs", "other", "ok", now);

		var star = Http("health:read", project: "*");
		var res = await HealthTools.SearchAsync(star, _db, "other");
		res.Services.Single().Svc.Should().Be("theirs");
	}

	[Fact]
	public async Task MissingScope_Throws()
	{
		var http = Http("health:write"); // write, not read
		await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
			HealthTools.SearchAsync(http, _db, Proj));
	}

	[Fact]
	public async Task History_OptIn_WindowAndLimitApply()
	{
		var now = DateTime.UtcNow;
		Push("api", Proj, "ok", now.AddSeconds(-500)); // outside a 300s window
		Push("api", Proj, "degraded", now.AddSeconds(-120));
		Push("api", Proj, "ok", now.AddSeconds(-30)); // newest

		var http = Http("health:read");

		// window bounds the age; the 500s-old entry is excluded.
		var windowed = await HealthTools.SearchAsync(http, _db, Proj, window: 300);
		var api = windowed.Services.Single();
		api.History.Should().NotBeNull();
		api.History!.Select(h => h.Status).Should().Equal("ok", "degraded"); // most-recent first, 500s one dropped

		// limit caps the count (most-recent first).
		var limited = await HealthTools.SearchAsync(http, _db, Proj, limit: 1);
		limited.Services.Single().History!.Should().ContainSingle()
			.Which.Status.Should().Be("ok");
	}

	[Fact]
	public async Task SvcFilter_NarrowsToOneService()
	{
		var now = DateTime.UtcNow;
		Push("api", Proj, "ok", now);
		Push("worker", Proj, "ok", now);

		var http = Http("health:read");
		var res = await HealthTools.SearchAsync(http, _db, Proj, svc: "worker");
		res.Services.Single().Svc.Should().Be("worker");
	}

	static IHttpContextAccessor Http(string scopes, string? project = null)
	{
		var id = new ClaimsIdentity([new Claim("project", project ?? Proj), new Claim("scopes", scopes)], "test");
		return new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(id) } };
	}
}
