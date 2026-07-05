using System.Threading;
using LinqToDB;
using Microsoft.Extensions.Options;
using PetBox.Core.Auth;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Tests.Auth;

// adminbootstrapper-seed-race: EnsureAdminUser used to do a plain check-then-insert with no
// atomicity, so two parallel first-boot callers (e.g. two processes racing the same fresh
// petbox.db) could both pass the "no $system admin yet" check before either had written
// anything, and each insert a User + WorkspaceMember row — a duplicate admin user and/or a
// duplicate $system-admin membership. The fix pairs the check-then-insert with a transaction and
// re-check, backstopped by DB-level uniqueness (Users.Username UNIQUE from M008,
// WorkspaceMembers(UserId, WorkspaceKey) UNIQUE from M035): SQLite serializes writers, so exactly
// one caller's transaction commits the User+WorkspaceMember pair, and every other caller's insert
// collides with a unique constraint and is swallowed as a no-op.
public sealed class AdminBootstrapperConcurrencyTests : IDisposable
{
	const string PasswordHash = "pbkdf2$100000$h1twJi/he3s8S7jSM9pkGQ==$efnLBffww5Gprn6BjpNgZkTcG+1zNu2L6z3TZ7YvD/o=";

	readonly string _dir;
	readonly string _cs;

	public AdminBootstrapperConcurrencyTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-adminboot-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		_cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(_cs);
	}

	public void Dispose() => TestDirs.CleanupOrDefer(_dir);

	[Fact]
	public async Task Parallel_first_boot_bootstrap_creates_exactly_one_admin()
	{
		var options = Options.Create(new AdminOptions { Username = "admin", PasswordHash = PasswordHash });

		// Each task gets its own PetBoxDb connection — like separate request scopes / processes —
		// and a Barrier lines them up so every EnsureAdminUser call starts from the same "no admin
		// yet" state instead of trickling in one at a time.
		const int Parallelism = 16;
		using var barrier = new Barrier(Parallelism);
		var tasks = Enumerable.Range(0, Parallelism).Select(_ => Task.Run(() =>
		{
			using var db = new PetBoxDb(PetBoxDb.CreateOptions(_cs));
			barrier.SignalAndWait();
			AdminBootstrapper.EnsureAdminUser(db, options);
		})).ToArray();

		await Task.WhenAll(tasks);

		using var verify = new PetBoxDb(PetBoxDb.CreateOptions(_cs));
		verify.Users.Count(u => u.Username == "admin").Should().Be(1, "concurrent first-boot calls must not duplicate the admin user");
		verify.WorkspaceMembers.Count(m => m.WorkspaceKey == "$system" && m.Role == WorkspaceRole.Admin).Should().Be(1,
			"concurrent first-boot calls must not duplicate the $system/Admin membership");
	}
}
