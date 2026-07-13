using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Tests.Data;

// IShareLinkDirectory is the door onto ShareLinks: a share token is a bearer credential, so FindAsync
// must resolve a token to EXACTLY the row it names — and only a caller who already holds the token can
// reach anything (there is no list/enumerate method to prove that against, by design: these tests prove
// the lookup itself does not widen what a token grants).
public sealed class ShareLinkDirectoryTests
{
	static (ShareLinkDirectory Svc, ICoreDbFactory Dbf) New()
	{
		var cs = TestSchema.NewTempConnectionString();
		TestSchema.Core(cs);
		var dbf = new CoreDbFactory(cs);
		return (new ShareLinkDirectory(dbf), dbf);
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
}
