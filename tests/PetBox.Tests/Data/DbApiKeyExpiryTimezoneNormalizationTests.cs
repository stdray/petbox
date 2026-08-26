using LinqToDB;
using PetBox.Core.Auth;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Tests.Data;

// work config-key-expiry-timezone-normalization / spec auth-key-expiry. Companion to
// ConfigApiKeyExpiryTimezoneNormalizationTests (Web/ConfigApiKeyExpiryTests.cs), which covers the
// config-sourced key path. This covers what the work card's item 2 asked to verify: the DB-sourced
// path (DbApiKeyLookup, ApiKeys minted through AgentKeyAdminService and read back via linq2db over
// Microsoft.Data.Sqlite).
//
// Measured directly (not assumed): every writer of ApiKeys.ExpiresAt derives it from
// `DateTime.UtcNow` (Kind=Utc going in), so the DB path was never mis-PARSED the way the config
// path was — but linq2db/Microsoft.Data.Sqlite drops the Kind label entirely on the way OUT. A
// value written with Kind=Utc reads back as Kind=Unspecified (same Ticks — the instant itself was
// never wrong). That mismatched Kind is exactly the class of latent bug PetBox.Log.Core's
// KqlSqlExpressions and Mcp/HealthTools.cs already carry a `SpecifyKind(..., Utc)` workaround for,
// on the same SQLite behavior: a caller that reasonably calls `.ToUniversalTime()` on an
// Unspecified-but-actually-UTC value later would silently reinterpret it as host-local and corrupt
// it right there. DbApiKeyLookup now normalizes the Kind on every read so ApiKey.ExpiresAt is
// always Kind=Utc regardless of source, matching ConfigApiKeyLookup.
public sealed class DbApiKeyExpiryTimezoneNormalizationTests
{
	[Fact]
	public async Task ExpiresAt_ReadBack_IsAlwaysUtcKind()
	{
		var cs = TestSchema.NewTempConnectionString();
		TestSchema.Core(cs);
		var factory = new CoreDbFactory(cs);

		var writtenAsUtc = DateTime.UtcNow.AddHours(1);
		using (var db = factory.Open())
		{
			await db.InsertAsync(new Workspace { Key = "tzdbws", Name = "s", CreatedAt = DateTime.UtcNow });
			await db.InsertAsync(new Project { Key = "tzdbproj", WorkspaceKey = "tzdbws", Name = "s" });
			await db.InsertAsync(new ApiKey
			{
				Key = "tzdb-key",
				ProjectKey = "tzdbproj",
				Scopes = "config:read",
				CreatedAt = DateTime.UtcNow,
				ExpiresAt = writtenAsUtc,
			});
		}

		var lookup = new DbApiKeyLookup(factory);
		var found = lookup.FindByKey("tzdb-key");

		found.Should().NotBeNull();
		found!.ExpiresAt.Should().NotBeNull();
		found.ExpiresAt!.Value.Kind.Should().Be(DateTimeKind.Utc);
		// Sub-tick SQLite storage precision (millisecond) means an exact equality on Ticks would be
		// flaky; the instant must survive to within that precision, not just the Kind label.
		found.ExpiresAt.Value.Should().BeCloseTo(writtenAsUtc, TimeSpan.FromMilliseconds(5));
	}

	[Fact]
	public async Task NoExpiresAt_StaysNull()
	{
		var cs = TestSchema.NewTempConnectionString();
		TestSchema.Core(cs);
		var factory = new CoreDbFactory(cs);

		using (var db = factory.Open())
		{
			await db.InsertAsync(new Workspace { Key = "tzdbws2", Name = "s", CreatedAt = DateTime.UtcNow });
			await db.InsertAsync(new Project { Key = "tzdbproj2", WorkspaceKey = "tzdbws2", Name = "s" });
			await db.InsertAsync(new ApiKey
			{
				Key = "tzdb-unbounded",
				ProjectKey = "tzdbproj2",
				Scopes = "config:read",
				CreatedAt = DateTime.UtcNow,
			});
		}

		var lookup = new DbApiKeyLookup(factory);
		lookup.FindByKey("tzdb-unbounded")!.ExpiresAt.Should().BeNull();
	}
}
