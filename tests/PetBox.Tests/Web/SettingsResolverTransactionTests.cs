using LinqToDB;
using LinqToDB.Async;
using PetBox.Config;
using PetBox.Core.Data;
using PetBox.Core.Settings;
using PetBox.Web.Settings;

namespace PetBox.Tests.Web;

// SetAsync writes a WHOLE settings form — several properties from one user edit. It used to write
// them as N independent autocommits on the request's shared connection, with no transaction at all,
// while a comment claimed the write path had to stay on the injected connection "to join the
// request's transaction" (there was none). The consequence was a real bug, not a theoretical one:
// Encode() throws mid-loop on a secret with no master key configured (and on a bad cast), so a save
// that failed on the 3rd of 5 properties left the first two COMMITTED — the admin page silently
// half-applied, and the settings row set was a state the user never asked for.
//
// Now the whole loop runs in ONE transaction on ONE call-owned connection: all properties, or none.
public sealed class SettingsResolverTransactionTests : IDisposable
{
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly SettingsResolver _resolver;

	public SettingsResolverTransactionTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-settx-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		// IsAvailable = false, so Encode() throws on the SECRET property — mid-loop, after the plain
		// property ahead of it has already been written inside the transaction.
		_resolver = new SettingsResolver(_db.Factory(), new NoSecrets());
	}

	// The regression: a save that dies partway writes NOTHING.
	[Fact]
	public async Task SetAsync_WhenAPropertyThrowsMidway_WritesNothing()
	{
		var act = async () => await _resolver.SetAsync(
			Scope.System, "$",
			new PartialSaveSettings { First = "written-first", Secret = "boom", Third = "never" },
			new PartialSaveSettings(),
			updatedBy: null);

		// Fails on `Secret` — the 2nd of 3 properties.
		await act.Should().ThrowAsync<InvalidOperationException>()
			.WithMessage("*PETBOX_MASTER_KEY*");

		// `First` was encoded and written BEFORE the throw. It must not have survived the rollback.
		var rows = await _db.Settings.Where(s => s.Path.StartsWith("test.partialsave.")).ToListAsync();
		rows.Should().BeEmpty(
			"the whole SetAsync is one transaction — a property that threw must roll back the "
			+ "properties written before it, or the admin page half-applies a form the user "
			+ "submitted as a single edit");
	}

	// Guard-the-guard: the test above would also pass if SetAsync simply wrote nothing, ever. This
	// pins the other half — a save with no failing property commits EVERY property.
	[Fact]
	public async Task SetAsync_WhenEveryPropertySucceeds_CommitsAll()
	{
		await _resolver.SetAsync(
			Scope.System, "$",
			new PlainSaveSettings { First = "one", Second = "two", Third = "three" },
			new PlainSaveSettings(),
			updatedBy: null);

		var rows = await _db.Settings.Where(s => s.Path.StartsWith("test.plainsave.")).ToListAsync();
		rows.Select(r => r.Value).Should().BeEquivalentTo(["one", "two", "three"]);
	}

	public void Dispose()
	{
		_db.Dispose();
		try { Directory.Delete(_dir, true); } catch { /* temp dir */ }
	}

	// Property order is declaration order (Type.GetProperties), so `First` is written, then `Secret`
	// throws. That ordering is what makes the partial write reachable at all.
	sealed class PartialSaveSettings
	{
		[Setting(TopLevel = Scope.System, Key = "test.partialsave.first")]
		public string First { get; set; } = string.Empty;

		[Setting(TopLevel = Scope.System, Key = "test.partialsave.secret", IsSecret = true)]
		public string Secret { get; set; } = string.Empty;

		[Setting(TopLevel = Scope.System, Key = "test.partialsave.third")]
		public string Third { get; set; } = string.Empty;
	}

	sealed class PlainSaveSettings
	{
		[Setting(TopLevel = Scope.System, Key = "test.plainsave.first")]
		public string First { get; set; } = string.Empty;

		[Setting(TopLevel = Scope.System, Key = "test.plainsave.second")]
		public string Second { get; set; } = string.Empty;

		[Setting(TopLevel = Scope.System, Key = "test.plainsave.third")]
		public string Third { get; set; } = string.Empty;
	}

	sealed class NoSecrets : ISecretEncryptor
	{
		public bool IsAvailable => false;
		public SecretBundle Encrypt(string plaintext) => throw new NotSupportedException();
		public string Decrypt(string ciphertextB64, string ivB64, string authTagB64) => throw new NotSupportedException();
	}
}
