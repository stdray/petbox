using Microsoft.Data.Sqlite;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Tests.Data;

// IShareLinkDirectory is the door onto ShareLinks: a share token is a bearer credential, so FindAsync
// must resolve a token to EXACTLY the row it names — and only a caller who already holds the token can
// reach anything (there is no list/enumerate method to prove that against, by design: these tests prove
// the lookup itself does not widen what a token grants).
public sealed class ShareLinkDirectoryTests : IDisposable
{
	readonly List<string> _dirs = [];

	(ShareLinkDirectory Svc, ICoreDbFactory Dbf) New()
	{
		var cs = TestSchema.NewTempConnectionString();
		_dirs.Add(Path.GetDirectoryName(new SqliteConnectionStringBuilder(cs).DataSource)!);
		TestSchema.Core(cs);
		var dbf = new CoreDbFactory(cs);
		return (new ShareLinkDirectory(dbf), dbf);
	}

	public void Dispose()
	{
		foreach (var dir in _dirs) TestDirs.CleanupOrDefer(dir);
	}

	static ShareLink NewLink(string id, DateTime expiresAt) => new()
	{
		Id = id,
		ProjectKey = "proj",
		LogName = "default",
		Kql = "events | take 10",
		CreatedAt = DateTime.UtcNow,
		ExpiresAt = expiresAt,
		SaltBase64 = Convert.ToBase64String(new byte[] { 1, 2, 3 }),
		ColumnsJson = "[\"Timestamp\",\"Message\"]",
		ModesJson = "{}",
		CreatedBy = "tester",
	};

	[Fact]
	public async Task FindAsync_ResolvesAValidTokenToExactlyItsOwnRow()
	{
		var (svc, _) = New();
		var link = NewLink("tok-valid", DateTime.UtcNow.AddHours(1));
		await svc.CreateAsync(link);

		var found = await svc.FindAsync("tok-valid");

		found.Should().NotBeNull();
		found!.Id.Should().Be("tok-valid");
		found.ProjectKey.Should().Be("proj");
		found.Kql.Should().Be("events | take 10");
	}

	[Fact]
	public async Task FindAsync_ReturnsNullForAnUnknownToken()
	{
		var (svc, _) = New();
		await svc.CreateAsync(NewLink("tok-real", DateTime.UtcNow.AddHours(1)));

		(await svc.FindAsync("tok-does-not-exist")).Should().BeNull(
			"an unguessed token must resolve to nothing — this is the credential check");
	}

	// The door does NOT fold expiry into FindAsync — it hands back the row exactly as stored, and the
	// caller (Share.cshtml.cs / ShareApi.GetTsvAsync) still compares ExpiresAt itself, exactly as before
	// this door existed. This test pins that: an expired row is still FOUND (not filtered out here),
	// so a behavior change in the door cannot silently change what callers see as "expired" vs "gone".
	[Fact]
	public async Task FindAsync_StillReturnsAnExpiredLink_ExpiryIsTheCallersCheckNotTheDoors()
	{
		var (svc, _) = New();
		var expired = NewLink("tok-expired", DateTime.UtcNow.AddHours(-1));
		await svc.CreateAsync(expired);

		var found = await svc.FindAsync("tok-expired");

		found.Should().NotBeNull();
		found!.ExpiresAt.Should().BeBefore(DateTime.UtcNow);
	}

	[Fact]
	public async Task CreateAsync_PersistsTheLinkTokenIsAddressedByIdAlone()
	{
		var (svc, dbf) = New();
		var link = NewLink("tok-created", DateTime.UtcNow.AddMinutes(30));

		await svc.CreateAsync(link);

		using var db = dbf.Open();
		var row = db.ShareLinks.FirstOrDefault(s => s.Id == "tok-created");
		row.Should().NotBeNull();
		row!.CreatedBy.Should().Be("tester");
	}

	// share-link-revocable: revoke must not depend on TTL — this link's ExpiresAt is far in the
	// future, and DeleteAsync still removes it.
	[Fact]
	public async Task DeleteAsync_OwnProject_HardDeletesTheRow_EvenFarFromExpiry()
	{
		var (svc, dbf) = New();
		var link = NewLink("tok-revoke-me", DateTime.UtcNow.AddDays(30));
		await svc.CreateAsync(link);

		var deleted = await svc.DeleteAsync("tok-revoke-me", "proj");

		deleted.Should().BeTrue();
		using var db = dbf.Open();
		db.ShareLinks.Any(s => s.Id == "tok-revoke-me").Should().BeFalse(
			"revoke is a hard delete — no row, readable or not, should remain");
	}

	// The ownership-confinement half of the trap: a caller that is honestly authorized for ITS OWN
	// project must not be able to remove a row that belongs to a DIFFERENT project just by knowing
	// the token value. (Id, ProjectKey) is the address, so a mismatched projectKey finds nothing —
	// mirrors AgentKeyAdminService.RevokeAsync's Owned(...) confinement.
	[Fact]
	public async Task DeleteAsync_ForeignProject_DeletesNothing_RowSurvives()
	{
		var (svc, dbf) = New();
		var link = NewLink("tok-foreign", DateTime.UtcNow.AddHours(1));
		await svc.CreateAsync(link);

		var deleted = await svc.DeleteAsync("tok-foreign", "some-other-project");

		deleted.Should().BeFalse();
		using var db = dbf.Open();
		db.ShareLinks.Any(s => s.Id == "tok-foreign").Should().BeTrue(
			"a caller claiming a different project must not be able to delete this row");
	}

	[Fact]
	public async Task DeleteAsync_UnknownToken_ReturnsFalse_SameAsForeignProject()
	{
		var (svc, _) = New();

		(await svc.DeleteAsync("tok-does-not-exist", "proj")).Should().BeFalse(
			"a nonexistent token must answer identically to a foreign-project token — no existence oracle");
	}
}
