using PetBox.Core.Contract;

namespace PetBox.Tests.Search;

// THREE REFUSALS, THREE DIAGNOSES — asserted on the TEXT, because the type cannot tell them apart.
//
// All three are ArgumentException, so every surface test that only proves "it threw" would stay green
// if the three messages were merged, swapped, or reduced to one. They are not interchangeable: each
// names a different cause and asks the caller for a different judgement.
//
//   * FINGERPRINT — "you changed the question". The caller's own arguments moved (a filter, the sort,
//     or the data version folded into it). The advice — keep the arguments identical — is ACTIONABLE,
//     and it is exactly the wrong advice for the other two, where the arguments were already identical.
//   * ORDER HASH — "the answer is ranked differently". Nothing was written and the arguments held, but
//     the list moved: a rerank route recovered or fell over, the vector index drained. Second echelon
//     since work/rerank-route-nondeterministic-order — it no longer decides the common case, and it is
//     still the only thing that can see an order moving with the pool still in hand.
//   * POOL — "your walk outlived its pool". The measured case. Nothing changed anywhere; the pool the
//     order came out of expired, and a second cross-encoder pass is not the first one. Telling this
//     caller "the ranking changed" sends them hunting a ranking bug that does not exist, which is the
//     false diagnosis this card exists to remove.
public sealed class CursorRefusalTextTests
{
	static readonly KeysetCursor Token = new("fp-1", "", "row-7", "board", "order-1");

	[Fact]
	public void TheThreeRefusals_EachNameTheirOwnCause()
	{
		FingerprintRefusal().Should().Contain("issued for a DIFFERENT query")
			.And.Contain("Keep the query identical", "this is the one refusal the caller can act on by changing an argument");

		OrderRefusal().Should().Contain("ranked DIFFERENTLY")
			.And.Contain("Your arguments are fine", "the caller did nothing wrong — the ranking moved");

		PoolRefusal().Should().Contain("ranked POOL this cursor was walking is gone")
			.And.Contain("outlived its pool", "the cause is expiry, and the caller needs to hear that word");
	}

	[Fact]
	public void NoRefusalCanBeMistakenForAnother()
	{
		// The property that actually protects the caller: each message must be missing the OTHER two's
		// distinguishing phrases. A merge, a copy-paste or a "unified" wording breaks this and nothing
		// else would notice.
		var fingerprint = FingerprintRefusal();
		var order = OrderRefusal();
		var pool = PoolRefusal();

		fingerprint.Should().NotContain("ranked DIFFERENTLY").And.NotContain("ranked POOL");
		order.Should().NotContain("DIFFERENT query").And.NotContain("ranked POOL");
		pool.Should().NotContain("DIFFERENT query").And.NotContain("ranked DIFFERENTLY");

		new[] { fingerprint, order, pool }.Should().OnlyHaveUniqueItems();
	}

	[Fact]
	public void TheSubjectNamesTheToolInEveryRefusal()
	{
		// A refusal reads as advice only if it says which read it came from — a walk that spans
		// memory_search and tasks_search must not leave the caller guessing which one stopped.
		FingerprintRefusal().Should().StartWith("memory_search: ");
		OrderRefusal().Should().StartWith("memory_search: ");
		PoolRefusal().Should().StartWith("memory_search: ");
	}

	[Fact]
	public void ALivePool_IsNotARefusalAtAll()
	{
		// The guard must be silent on the path that is fine, or it is a wall rather than a check.
		var act = () => KeysetCursor.AssertPoolAlive(false, "memory_search");

		act.Should().NotThrow();
	}

	static string FingerprintRefusal() =>
		Capture(() => Token.AssertFingerprint("fp-2", "memory_search"));

	static string OrderRefusal() =>
		Capture(() => Token.AssertPoolOrder("order-2", "memory_search"));

	static string PoolRefusal() =>
		Capture(() => KeysetCursor.AssertPoolAlive(true, "memory_search"));

	static string Capture(Action act)
	{
		var thrown = Record.Exception(act);
		thrown.Should().BeOfType<ArgumentException>();
		return thrown!.Message;
	}
}
