using PetBox.Memory.Contract;

namespace PetBox.Tests.Memory;

// The exclusion set behind every AUTOMATIC whole-container sweep (spec: autocapture-dedup, work
// `autocapture-dedup-blind-to-canon`). The autocapture judge now reads "every store this project
// has, minus these" and ships what it reads to an EXTERNAL LLM, so this set is the entire boundary
// between "the client's curated knowledge finally reaches the dedup judge" and "the client's
// secrets do".
//
// Two legs live in one set for two DIFFERENT reasons, and the tests below pin that they cannot be
// confused: the sensitive leg is a rule, the digest leg is a tunable recall policy.
public sealed class MemoryStoreAutoSweepExclusionTests
{
	[Fact]
	public void SensitiveStores_AreNeverSweepable()
	{
		foreach (var sensitive in MemoryStores.SensitiveNames)
			MemoryStores.IsAutoSweepable(sensitive).Should().BeFalse();
		MemoryStores.IsAutoSweepable("ops").Should().BeFalse();
		MemoryStores.IsAutoSweepable("OPS").Should().BeFalse(); // case-insensitive, like every store predicate here
	}

	[Fact]
	public void TheSensitiveLeg_IsContainedInTheExclusionSet_SoNoSweepCanMissIt()
	{
		// A caller that consults only AutoSweepExcludedNames must still be safe. (A caller that
		// consults only IsSensitive must NOT be assumed safe — hence the digest assertions below.)
		MemoryStores.SensitiveNames.Should().BeSubsetOf(MemoryStores.AutoSweepExcludedNames);
	}

	[Fact]
	public void SessionDigests_IsExcluded_ButIsNotSensitive()
	{
		// The distinction matters: `session-digests` is excluded from a sweep for double-counting,
		// and is perfectly linkable elsewhere. Collapsing the two reasons would either leak `ops`
		// into link surfaces or forbid digests from being linked at all.
		MemoryStores.AutoSweepExcludedNames.Should().Contain("session-digests");
		MemoryStores.IsAutoSweepable("session-digests").Should().BeFalse();
		MemoryStores.IsSensitive("session-digests").Should().BeFalse();
	}

	[Fact]
	public void KnowledgeStores_AreSweepable_IncludingOnesWeHaveNeverHeardOf()
	{
		// The point of the card. `canon` was the reported blind spot; the unnamed store is the one
		// no literal of ours could ever have covered.
		MemoryStores.IsAutoSweepable("canon").Should().BeTrue();
		MemoryStores.IsAutoSweepable("notes").Should().BeTrue();
		MemoryStores.IsAutoSweepable("autocaptured").Should().BeTrue();
		MemoryStores.IsAutoSweepable("dacha-notes").Should().BeTrue();
		MemoryStores.IsAutoSweepable("whatever-a-client-invents").Should().BeTrue();
	}

	[Fact]
	public void NoStoreName_IsNotSweepable()
	{
		// Null/empty is not a store; a sweep has nothing to read and must not treat the absence of
		// a name as permission.
		MemoryStores.IsAutoSweepable(null).Should().BeFalse();
		MemoryStores.IsAutoSweepable("").Should().BeFalse();
		MemoryStores.IsAutoSweepable("   ").Should().BeFalse();
	}
}
