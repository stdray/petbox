using LinqToDB;
using PetBox.Core.Contract;
using PetBox.Core.Data;
using PetBox.Core.Data.Temporal;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Memory.Contract;
using PetBox.Memory.Data;
using PetBox.Memory.Services;

namespace PetBox.Tests.Fragments;

// memory_upsert `fragment` (work/write-fragment-patch). The third merge site — MemoryService's
// ToEntry(), whose `current` row is looked up only when Version != 0 — and therefore the one where
// "a fragment needs an existing revision" has a second way to be true (baseline 0).
//
// This verb is also where the card's economics bite hardest: the canon store has a 10k write
// budget, so an index that is nearly full cannot be edited at all by full-replace without
// re-emitting the whole thing. The last test here pins that a fragment edit of a canon entry works.
public sealed class MemoryFragmentPatchTests : IDisposable
{
	const string Proj = "proj";
	const string Store = "notes";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<MemoryDb> _factory;
	readonly MemoryService _memory;

	public MemoryFragmentPatchTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-fragmem-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_factory = new ScopedDbFactory<MemoryDb>(Path.Combine(_dir, "memory"), Scope.Project,
			c => new MemoryDb(MemoryDb.CreateOptions(c)), TestSchema.Memory);
		_memory = new MemoryService(new MemoryStore(_db.Factory(), _factory));
	}

	public void Dispose()
	{
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	const string Original = "line one\nline two\nline three";

	static FragmentEdit E(string? old, string? @new) => new(old, @new);

	async Task<long> SeedAsync(string key = "k", string body = Original, string store = Store)
	{
		var r = await _memory.UpsertAsync(Proj, store,
			[new MemoryEntryInput { Key = key, Version = 0, Type = "Project", Description = "d", Body = body }], []);
		r.Result.Applied.Should().BeTrue();
		return (await _memory.GetAsync(Proj, store, key))!.Version;
	}

	async Task<MemoryEntryView> ReadAsync(string key = "k", string store = Store) =>
		(await _memory.GetAsync(Proj, store, key))!;

	Task<MemoryUpsertOutcome> UpsertAsync(params MemoryEntryInput[] entries) =>
		_memory.UpsertAsync(Proj, Store, entries, []);

	// ── CONTROL ──────────────────────────────────────────────────────────────────────

	[Fact]
	public async Task UniqueFragment_PatchesTheEntry()
	{
		var v = await SeedAsync();

		var r = await UpsertAsync(new MemoryEntryInput { Key = "k", Version = v, Fragment = [E("line two", "LINE TWO")] });

		r.Result.Applied.Should().BeTrue();
		(await ReadAsync()).Body.Should().Be("line one\nLINE TWO\nline three");
	}

	[Fact]
	public async Task Fragment_KeepsDescriptionTagsAndType()
	{
		var seed = await _memory.UpsertAsync(Proj, Store,
			[new MemoryEntryInput { Key = "k", Version = 0, Type = "Reference", Description = "keep me", Body = Original, Tags = ["t:one"] }], []);
		seed.Result.Applied.Should().BeTrue();
		var v = (await ReadAsync()).Version;

		await UpsertAsync(new MemoryEntryInput { Key = "k", Version = v, Fragment = [E("line one", "1")] });

		var e = await ReadAsync();
		e.Description.Should().Be("keep me");
		e.Type.Should().Be("Reference");
		e.Tags.Should().BeEquivalentTo(new[] { "t:one" });
		e.Body.Should().Be("1\nline two\nline three");
	}

	// ── refusals ─────────────────────────────────────────────────────────────────────

	[Fact]
	public async Task FragmentMatchingTwice_IsRefused_BodyUnchanged()
	{
		var v = await SeedAsync(body: "dup HERE and dup HERE");

		var r = await UpsertAsync(new MemoryEntryInput { Key = "k", Version = v, Fragment = [E("dup HERE", "X")] });

		r.Result.Applied.Should().BeFalse();
		var c = r.Result.Conflicts.Should().ContainSingle().Subject;
		c.Key.Should().Be("k");
		c.Kind.Should().Be(TemporalConflictKind.Rejected);
		c.Reason.Should().Contain("occurs 2 times");

		var e = await ReadAsync();
		e.Body.Should().Be("dup HERE and dup HERE");
		e.Version.Should().Be(v);
	}

	[Fact]
	public async Task FragmentMatchingZeroTimes_IsRefused_BodyUnchanged()
	{
		var v = await SeedAsync();

		var r = await UpsertAsync(new MemoryEntryInput { Key = "k", Version = v, Fragment = [E("absent", "X")] });

		r.Result.Applied.Should().BeFalse();
		r.Result.Conflicts.Should().ContainSingle().Which.Reason.Should().Contain("does not occur");

		var e = await ReadAsync();
		e.Body.Should().Be(Original);
		e.Version.Should().Be(v);
	}

	[Fact]
	public async Task MultiEditList_IsAllOrNothing()
	{
		var v = await SeedAsync();

		var r = await UpsertAsync(new MemoryEntryInput
		{
			Key = "k",
			Version = v,
			Fragment = [E("line one", "1"), E("line two", "2"), E("NOT THERE", "x")],
		});

		r.Result.Applied.Should().BeFalse();
		r.Result.Conflicts.Should().ContainSingle().Which.Reason.Should().Contain("fragment[2]");

		var e = await ReadAsync();
		e.Body.Should().Be(Original);          // neither "1" nor "2" survived
		e.Version.Should().Be(v);
	}

	[Fact]
	public async Task MultiEditList_AllMatching_AppliesEveryEdit()
	{
		var v = await SeedAsync();

		var r = await UpsertAsync(new MemoryEntryInput
		{
			Key = "k",
			Version = v,
			Fragment = [E("line one", "1"), E("line two", "2"), E("line three", "3")],
		});

		r.Result.Applied.Should().BeTrue();
		(await ReadAsync()).Body.Should().Be("1\n2\n3");
	}

	[Fact]
	public async Task BodyAndFragmentTogether_IsRefused()
	{
		var v = await SeedAsync();

		var r = await UpsertAsync(new MemoryEntryInput
		{
			Key = "k",
			Version = v,
			Body = "wholesale replacement",
			Fragment = [E("line one", "1")],
		});

		r.Result.Applied.Should().BeFalse();
		r.Result.Conflicts.Should().ContainSingle().Which.Reason.Should().Contain("mutually exclusive");
		(await ReadAsync()).Body.Should().Be(Original);
	}

	[Fact]
	public async Task FragmentWithBaselineZero_IsRefused_NoEntryIsCreated()
	{
		// Version 0 means "I read nothing" — MemoryService does not even look the row up, so
		// there is no text to match. Refused rather than silently creating the entry.
		var r = await UpsertAsync(new MemoryEntryInput { Key = "fresh", Version = 0, Type = "Project", Fragment = [E("x", "y")] });

		r.Result.Applied.Should().BeFalse();
		r.Result.Conflicts.Should().ContainSingle().Which.Reason.Should().Contain("no active revision");
		(await _memory.GetAsync(Proj, Store, "fresh")).Should().BeNull();
	}

	[Fact]
	public async Task Partial_ABadFragmentIsRejectedPerEntry_TheGoodOneLands()
	{
		var badV = await SeedAsync("bad", Original);
		var goodV = await SeedAsync("good", "some other text");

		var r = await _memory.UpsertAsync(Proj, Store,
			[
				new MemoryEntryInput { Key = "bad", Version = badV, Fragment = [E("MISSING", "x")] },
				new MemoryEntryInput { Key = "good", Version = goodV, Fragment = [E("other", "different")] },
			],
			[], atomic: false);

		r.Result.Applied.Should().BeTrue();
		r.Result.Conflicts.Should().ContainSingle().Which.Key.Should().Be("bad");
		(await ReadAsync("bad")).Body.Should().Be(Original);
		(await ReadAsync("good")).Body.Should().Be("some different text");
	}

	// ── the economics the card is actually about ─────────────────────────────────────

	[Fact]
	public async Task CanonEntryNearItsBudget_CanBeEditedByFragment()
	{
		// The canon store refuses a body over 10000 chars. A ~9.9k index is therefore editable
		// ONLY if the caller can send just the change: this is the case the card was raised on.
		var big = "HEADER\n" + new string('x', 9900) + "\nFOOTER";
		big.Length.Should().BeLessThan(10000);
		var v = await SeedAsync("index", big, store: "canon");

		var r = await _memory.UpsertAsync(Proj, "canon",
			[new MemoryEntryInput { Key = "index", Version = v, Fragment = [E("FOOTER", "TRAILER")] }], []);

		r.Result.Applied.Should().BeTrue();
		var e = await ReadAsync("index", "canon");
		e.Body.Should().EndWith("TRAILER");
		e.Body.Should().StartWith("HEADER");
		e.Body.Length.Should().Be(big.Length + 1);
	}
}
