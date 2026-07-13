using FluentMigrator.Runner;
using LinqToDB;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Models;

// NOT namespace PetBox.Tests.Core — that name shadows PetBox.Core for every other file in this
// assembly ("PetBox.Core.Models does not exist in PetBox.Tests.Core"). The sibling files in this
// folder avoid it for the same reason (see FluentMappingCompletenessTests → PetBox.Tests.Mapping).
namespace PetBox.Tests.Provisioning;

// M044_UserWorkspaceQuota + the Users.WorkspaceQuota mapping.
//
// The backfill is the part that cannot be re-derived from the code later: every account that existed
// BEFORE the column gets 1 (a one-time grant, so the humans already on the instance are not bricked),
// while an account created AFTER it gets only what was explicitly asked for. Those two rules look the
// same from the outside if you only ever test a fresh DB — so this test migrates to the version
// BEFORE the column, plants accounts, and only then runs it.
public sealed class UserWorkspaceQuotaMigrationTests : IDisposable
{
	readonly string _path = Path.Combine(Path.GetTempPath(), "petbox-quota-mig-" + Guid.NewGuid().ToString("N") + ".db");

	string ConnectionString => $"Data Source={_path}";

	// The Core migration set, stopped at an explicit version — MigrationRunner.Run always goes to the
	// latest, which is exactly what this test must NOT do for the first leg.
	void MigrateTo(long version)
	{
		var services = new ServiceCollection()
			.AddFluentMigratorCore()
			.ConfigureRunner(rb => rb
				.AddSQLite()
				.WithGlobalConnectionString(ConnectionString)
				.ScanIn(typeof(PetBoxDb).Assembly).For.Migrations())
			.BuildServiceProvider();

		using var scope = services.CreateScope();
		scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp(version);
	}

	// Raw SQL on purpose: it inserts a row the way a pre-M044 build did — without naming the column
	// that does not exist yet (and, in the post-migration case, without naming one the app always sets).
	void InsertUserWithoutNamingTheQuota(string username)
	{
		using var conn = new SqliteConnection(ConnectionString);
		conn.Open();
		using var cmd = conn.CreateCommand();
		cmd.CommandText = "INSERT INTO Users (Username, PasswordHash, CreatedAt) VALUES ($u, 'x', '2026-01-01')";
		cmd.Parameters.AddWithValue("$u", username);
		cmd.ExecuteNonQuery();
	}

	[Fact]
	public void Accounts_that_predate_the_column_are_backfilled_with_one()
	{
		// The world as it was: users exist, the column does not.
		MigrateTo(43);
		InsertUserWithoutNamingTheQuota("old-hand");
		InsertUserWithoutNamingTheQuota("old-timer");

		MigrateTo(44);

		using var db = new PetBoxDb(PetBoxDb.CreateOptions(ConnectionString));
		db.Users.Where(u => u.Username == "old-hand").Single().WorkspaceQuota.Should().Be(1,
			"an account that existed before the right was expressible gets a one-time allowance of 1 — "
			+ "otherwise the migration would brick every human already using the instance");
		db.Users.Where(u => u.Username == "old-timer").Single().WorkspaceQuota.Should().Be(1);
	}

	[Fact]
	public void An_account_created_after_the_migration_gets_no_free_allowance()
	{
		MigrateTo(44);

		// The backfill is NOT a system default. A row inserted without naming the column falls to the
		// column default (0 — "may not create"), and the admin UI refuses to submit without a value at
		// all, so the number on a new account is always someone's decision.
		InsertUserWithoutNamingTheQuota("newcomer");

		using var db = new PetBoxDb(PetBoxDb.CreateOptions(ConnectionString));
		db.Users.Where(u => u.Username == "newcomer").Single().WorkspaceQuota.Should().Be(0,
			"the '1' is a backfill of the past, never a default for the future");
	}

	// The linq2db Fluent-mapping trap (see FluentMappingCompletenessTests): a column that exists in the
	// migration but is not declared in PetBoxDb is silently dropped from INSERTs and reads back as the
	// CLR default — while reporting success. An INSERT → SELECT round-trip is the only thing that
	// catches it.
	[Fact]
	public async Task WorkspaceQuota_round_trips_through_the_mapping()
	{
		MigrateTo(44);

		using var db = new PetBoxDb(PetBoxDb.CreateOptions(ConnectionString));
		await db.InsertAsync(new User
		{
			Username = "round-trip",
			PasswordHash = "x",
			CreatedAt = DateTime.UtcNow,
			WorkspaceQuota = 7,
		});

		db.Users.Where(u => u.Username == "round-trip").Single().WorkspaceQuota.Should().Be(7,
			"an undeclared column would come back 0 here — and the insert would have reported success");
	}

	public void Dispose()
	{
		SqliteConnection.ClearAllPools();
		try { if (File.Exists(_path)) File.Delete(_path); }
		catch (IOException) { /* a pooled handle outliving the test — a temp file left behind is harmless */ }
	}
}
