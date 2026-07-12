using LinqToDB;
using LinqToDB.Async;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Tests.Data;

// ICoreDbFactory is how core.db is meant to be reached: a fresh, caller-owned connection per call,
// so no DataConnection is ever shared across threads (a linq2db DataConnection is not thread-safe;
// the request-shared one is what 500'd the cross-scope search).
public sealed class CoreDbFactoryTests
{
	static ICoreDbFactory NewFactory(out string cs)
	{
		cs = TestSchema.NewTempConnectionString();
		TestSchema.Core(cs);
		return new CoreDbFactory(cs);
	}

	[Fact]
	public void Open_ReturnsAFreshConnectionEachCall()
	{
		var factory = NewFactory(out _);

		using var a = factory.Open();
		using var b = factory.Open();

		a.Should().NotBeSameAs(b,
			"the whole point of the factory is that no connection is shared — every caller gets its own");
	}

	// THE regression this must never lose. PetBoxDb.CreateOptions attaches a SHARED MappingSchema;
	// giving each connection its OWN makes linq2db's per-schema MappingAttributesCache grow without
	// bound (~290 MB / 3M+ nodes by mid-day => the production OOM). The factory multiplies the number
	// of PetBoxDb instances by design, so a per-instance MappingSchema here would not be a slow leak —
	// it would be a fast one.
	[Fact]
	public void Open_SharesOneMappingSchemaAcrossEveryConnection()
	{
		var factory = NewFactory(out _);

		using var a = factory.Open();
		using var b = factory.Open();

		a.MappingSchema.Should().BeSameAs(b.MappingSchema,
			"every PetBoxDb must reuse PetBoxDb.SharedMappingSchema — a per-connection MappingSchema "
			+ "is the ~290 MB MappingAttributesCache growth that drove the prod OOM");
	}

	// A connection from the factory is a real, working connection against the migrated schema —
	// the options clone must preserve the provider and the connection string, not just the mapping.
	[Fact]
	public async Task Open_YieldsAWorkingConnection_ThatRoundTrips()
	{
		var factory = NewFactory(out _);

		await using (var write = factory.Open())
			await write.InsertAsync(new Workspace { Key = "ws-factory", Name = "Factory" });

		// A SEPARATE connection sees the committed row: the factory's connections all address the
		// same file, and each is independently usable.
		await using var read = factory.Open();
		// Lambda param typed explicitly: .NET 10's System.Linq.AsyncEnumerable overload otherwise
		// makes FirstOrDefaultAsync ambiguous with linq2db's (the suite's existing idiom).
		var found = await read.Workspaces.FirstOrDefaultAsync((Workspace w) => w.Key == "ws-factory");
		found.Should().NotBeNull();
		found!.Name.Should().Be("Factory");
	}
}
