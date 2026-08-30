using Microsoft.Data.Sqlite;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Tests.Data;

// INodeShareDirectory is the door onto `node_shares` — the second family of capability tokens in
// the tree (spec `node-share`). It is deliberately shaped like IShareLinkDirectory, so this file is
// deliberately shaped like ShareLinkDirectoryTests: same questions, same order, so a divergence
// between the two doors shows up as a MISSING test here rather than as an argument.
//
// The one thing that is genuinely new — and therefore where most of the assertions are — is a
// NULLABLE ExpiresAt (spec `node-share-lifetime`). "Never expires" and "expired at the epoch" are
// one bit apart in storage and opposite in meaning, so the null has to be pinned at the door, not
// only at the surface that mints it.
public sealed class NodeShareDirectoryTests : IDisposable
{
	readonly List<string> _dirs = [];

	(NodeShareDirectory Svc, ICoreDbFactory Dbf) New()
	{
		var cs = TestSchema.NewTempConnectionString();
		_dirs.Add(Path.GetDirectoryName(new SqliteConnectionStringBuilder(cs).DataSource)!);
		TestSchema.Core(cs);
		var dbf = new CoreDbFactory(cs);
		return (new NodeShareDirectory(dbf), dbf);
	}

	public void Dispose()
	{
		foreach (var dir in _dirs) TestDirs.CleanupOrDefer(dir);
	}

	const string Node = "0123456789abcdef0123456789abcdef";
	const string CommentKey = "fedcba9876543210fedcba9876543210";

	static NodeShare NewShare(string id, DateTime? expiresAt, string scope = NodeShareScopes.Body) => new()
	{
		Id = id,
		ProjectKey = "proj",
		Board = "work",
		NodeId = Node,
		CommentId = scope == NodeShareScopes.Comment ? CommentKey : null,
		Scope = scope,
		CreatedAt = DateTime.UtcNow,
		CreatedBy = "tester",
		ExpiresAt = expiresAt,
	};

	// Every stored column read back, not just the id. This is the linq2db Fluent trap in assertion
	// form (see FluentMappingCompletenessTests' header): an undeclared column is dropped from the
	// schema cache, the INSERT omits it, the read returns the CLR default and the call still reports
	// success — which here would mean a link that publishes the wrong board, the wrong node or a
	// wider scope than was granted.
	[Fact]
	public async Task FindAsync_ResolvesAValidTokenToExactlyItsOwnRow_EveryColumnRoundTrips()
	{
		var (svc, _) = New();
		var expires = new DateTime(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc);
		await svc.CreateAsync(NewShare("tok-valid", expires, NodeShareScopes.Comment));

		var found = await svc.FindAsync("tok-valid");

		found.Should().NotBeNull();
		found!.Id.Should().Be("tok-valid");
		found.ProjectKey.Should().Be("proj");
		found.Board.Should().Be("work");
		found.NodeId.Should().Be(Node);
		found.CommentId.Should().Be(CommentKey);
		found.Scope.Should().Be(NodeShareScopes.Comment);
		found.CreatedBy.Should().Be("tester");
		found.ExpiresAt.Should().Be(expires);
	}

	[Fact]
	public async Task FindAsync_ReturnsNullForAnUnknownToken()
	{
		var (svc, _) = New();
		await svc.CreateAsync(NewShare("tok-real", null));

		(await svc.FindAsync("tok-does-not-exist")).Should().BeNull(
			"an unguessed token must resolve to nothing — this is the credential check");
	}

	// spec `node-share-lifetime`. The failure this guards is silent and total: if ExpiresAt were
	// dropped from the mapping (or stored as a CLR default) a null would come back as
	// DateTime.MinValue, every link would read as long expired, and the feature would be dead while
	// the mint endpoint answered 200. So the null is asserted as a NULL, and then asserted again
	// through the one predicate every reader uses.
	[Fact]
	public async Task FindAsync_NullExpiresAt_RoundTripsAsNull_AndIsNeverExpired()
	{
		var (svc, _) = New();
		await svc.CreateAsync(NewShare("tok-forever", null));

		var found = await svc.FindAsync("tok-forever");

		found.Should().NotBeNull();
		found!.ExpiresAt.Should().BeNull(
			"null means 'no expiry at all' — DateTime.MinValue would mean 'expired in year 1'");
		found.IsExpiredAt(DateTime.UtcNow).Should().BeFalse();
		found.IsExpiredAt(DateTime.UtcNow.AddYears(500)).Should().BeFalse(
			"a link with no expiry does not acquire one by the passage of time");
	}

	// The mirror of ShareLinkDirectoryTests' expiry test: the door hands back the row as stored and
	// does NOT filter, so expiry stays the caller's decision and a change here cannot silently
	// redefine what "expired" means for the public reader.
	[Fact]
	public async Task FindAsync_StillReturnsAnExpiredShare_ExpiryIsTheCallersCheckNotTheDoors()
	{
		var (svc, _) = New();
		await svc.CreateAsync(NewShare("tok-expired", DateTime.UtcNow.AddHours(-1)));

		var found = await svc.FindAsync("tok-expired");

		found.Should().NotBeNull();
		found!.IsExpiredAt(DateTime.UtcNow).Should().BeTrue();
	}

	[Fact]
	public async Task CreateAsync_PersistsTheShare_TokenIsAddressedByIdAlone()
	{
		var (svc, dbf) = New();

		await svc.CreateAsync(NewShare("tok-created", DateTime.UtcNow.AddMinutes(30)));

		using var db = dbf.Open();
		db.NodeShares.FirstOrDefault(s => s.Id == "tok-created").Should().NotBeNull();
	}

	// Revoke must not depend on expiry — and for a node link that is not a nicety but the ONLY way
	// out: a link minted with no TTL at all is never withdrawn by waiting, so if this path failed
	// the grant would be permanent.
	[Fact]
	public async Task DeleteAsync_OwnProject_HardDeletesTheRow_EvenWithNoExpiryAtAll()
	{
		var (svc, dbf) = New();
		await svc.CreateAsync(NewShare("tok-revoke-me", null));

		var deleted = await svc.DeleteAsync("tok-revoke-me", "proj");

		deleted.Should().BeTrue();
		using var db = dbf.Open();
		db.NodeShares.Any(s => s.Id == "tok-revoke-me").Should().BeFalse(
			"revoke is a hard delete — a never-expiring link has no other way to stop serving");
	}

	[Fact]
	public async Task DeleteAsync_ForeignProject_DeletesNothing_RowSurvives()
	{
		var (svc, dbf) = New();
		await svc.CreateAsync(NewShare("tok-foreign", null));

		var deleted = await svc.DeleteAsync("tok-foreign", "some-other-project");

		deleted.Should().BeFalse();
		using var db = dbf.Open();
		db.NodeShares.Any(s => s.Id == "tok-foreign").Should().BeTrue(
			"(Id, ProjectKey) is the address — knowing the token value is not enough");
	}

	[Fact]
	public async Task DeleteAsync_UnknownToken_ReturnsFalse_SameAsForeignProject()
	{
		var (svc, _) = New();

		(await svc.DeleteAsync("tok-does-not-exist", "proj")).Should().BeFalse(
			"a nonexistent token must answer identically to a foreign-project token — no existence oracle");
	}

	// ── THE SHARED REVOKE (IShareRevocationService) ──────────────────────────────────────────────
	//
	// One route and one MCP verb serve BOTH token families, so the ordering rule lives in one
	// service. These pin what that service must be true of, at the unit level, with both real
	// directories over one core.db — the transport-level proof is in NodeShareApiAuthzTests.

	[Fact]
	public async Task Revocation_FindsATokenInEitherDirectory_AndStillConfinesItToItsProject()
	{
		var cs = TestSchema.NewTempConnectionString();
		_dirs.Add(Path.GetDirectoryName(new SqliteConnectionStringBuilder(cs).DataSource)!);
		TestSchema.Core(cs);
		var dbf = new CoreDbFactory(cs);
		var links = new ShareLinkDirectory(dbf);
		var nodes = new NodeShareDirectory(dbf);
		var revocation = new ShareRevocationService(links, nodes);

		await links.CreateAsync(new ShareLink
		{
			Id = "tok-log",
			ProjectKey = "proj",
			LogName = "default",
			Kql = "events | take 1",
			CreatedAt = DateTime.UtcNow,
			ExpiresAt = DateTime.UtcNow.AddDays(30),
			SaltBase64 = Convert.ToBase64String([1, 2, 3]),
			CreatedBy = "tester",
		});
		await nodes.CreateAsync(NewShare("tok-node", null));

		(await revocation.RevokeAsync("tok-log", "proj")).Should().BeTrue(
			"the log family must keep being revoked exactly as before — it is looked up first");
		(await revocation.RevokeAsync("tok-node", "proj")).Should().BeTrue(
			"…and a node link, whose token is indistinguishable from a log one, must be found too");

		using var db = dbf.Open();
		db.ShareLinks.Any(s => s.Id == "tok-log").Should().BeFalse();
		db.NodeShares.Any(s => s.Id == "tok-node").Should().BeFalse();
	}

	[Fact]
	public async Task Revocation_ForeignProjectOrUnknownToken_AnswerFalse_Indistinguishably()
	{
		var cs = TestSchema.NewTempConnectionString();
		_dirs.Add(Path.GetDirectoryName(new SqliteConnectionStringBuilder(cs).DataSource)!);
		TestSchema.Core(cs);
		var dbf = new CoreDbFactory(cs);
		var nodes = new NodeShareDirectory(dbf);
		var revocation = new ShareRevocationService(new ShareLinkDirectory(dbf), nodes);
		await nodes.CreateAsync(NewShare("tok-node", null));

		(await revocation.RevokeAsync("tok-node", "other-project")).Should().BeFalse();
		(await revocation.RevokeAsync("tok-never-existed", "proj")).Should().BeFalse(
			"the two must be one answer — otherwise revoke is an existence oracle over node links too");

		using var db = dbf.Open();
		db.NodeShares.Any(s => s.Id == "tok-node").Should().BeTrue(
			"the foreign attempt must not have landed");
	}
}
