using PetBox.Core.Contract;

namespace PetBox.Tests.Search;

// FOUR REFUSALS, FOUR DIAGNOSES — asserted on the TEXT, because the type cannot tell them apart.
//
// All four are ArgumentException, so every surface test that only proves "it threw" would stay green
// if the four messages were merged, swapped, or reduced to fewer. They are not interchangeable: each
// names a different cause and asks the caller for a different judgement.
//
//   * FINGERPRINT — "you changed the question". The caller's OWN arguments moved (a filter, the sort).
//     The advice — keep the arguments identical — is ACTIONABLE, and it is exactly the wrong advice for
//     the other three, where the arguments were already identical.
//   * DATA — "the data changed, not your question" (card: cursor-refusal-blames-caller-for-data-shift).
//     Something was WRITTEN to the project between pages. Used to be folded INTO the fingerprint (the
//     data stamp was one more part of `f`), which made this indistinguishable from FINGERPRINT — a write
//     mid-walk got the exact same "issued for a DIFFERENT query... keep the query identical" text as a
//     caller who actually changed an argument, even though the caller here changed nothing. That was the
//     false diagnosis THIS half of the card exists to remove.
//   * ORDER HASH — "the answer is ranked differently". Nothing was written and the arguments held, but
//     the list moved: a rerank route recovered or fell over, the vector index drained. Second echelon
//     since work/rerank-route-nondeterministic-order — it no longer decides the common case, and it is
//     still the only thing that can see an order moving with the pool still in hand.
//   * POOL — "your walk outlived its pool". The measured case. Nothing changed anywhere; the pool the
//     order came out of expired, and a second cross-encoder pass is not the first one. Telling this
//     caller "the ranking changed" (or "the data changed") sends them hunting a bug that does not exist.
public sealed class CursorRefusalTextTests
{
	static readonly KeysetCursor Token = new("fp-1", "", "row-7", "board", "order-1", "data-1");

	[Fact]
	public void TheFourRefusals_EachNameTheirOwnCause()
	{
		FingerprintRefusal().Should().Contain("issued for a DIFFERENT query")
			.And.Contain("Keep the query identical", "this is the one refusal the caller can act on by changing an argument");

		DataStampRefusal().Should().Contain("DATA this cursor was reading has changed")
			.And.Contain("Your arguments are fine", "the caller's own arguments did not change — a write did");

		OrderRefusal().Should().Contain("ranked DIFFERENTLY")
			.And.Contain("Your arguments are fine", "the caller did nothing wrong — the ranking moved");

		PoolRefusal().Should().Contain("ranked POOL this cursor was walking is gone")
			.And.Contain("outlived its pool", "the cause is expiry, and the caller needs to hear that word");
	}

	[Fact]
	public void NoRefusalCanBeMistakenForAnother()
	{
		// The property that actually protects the caller: each message must be missing the OTHER three's
		// distinguishing phrases. A merge, a copy-paste or a "unified" wording breaks this and nothing
		// else would notice.
		var fingerprint = FingerprintRefusal();
		var data = DataStampRefusal();
		var order = OrderRefusal();
		var pool = PoolRefusal();

		fingerprint.Should().NotContain("DATA this cursor").And.NotContain("ranked DIFFERENTLY").And.NotContain("ranked POOL");
		data.Should().NotContain("DIFFERENT query").And.NotContain("ranked DIFFERENTLY").And.NotContain("ranked POOL");
		order.Should().NotContain("DIFFERENT query").And.NotContain("DATA this cursor").And.NotContain("ranked POOL");
		pool.Should().NotContain("DIFFERENT query").And.NotContain("DATA this cursor").And.NotContain("ranked DIFFERENTLY");

		new[] { fingerprint, data, order, pool }.Should().OnlyHaveUniqueItems();
	}

	[Fact]
	public void TheSubjectNamesTheToolInEveryRefusal()
	{
		// A refusal reads as advice only if it says which read it came from — a walk that spans
		// memory_search and tasks_search must not leave the caller guessing which one stopped.
		FingerprintRefusal().Should().StartWith("memory_search: ");
		DataStampRefusal().Should().StartWith("memory_search: ");
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

	[Fact]
	public void AnUnchangedDataStamp_IsNotARefusalAtAll()
	{
		// Same shape as ALivePool_IsNotARefusalAtAll, for the fourth guard: it must be silent when the
		// data really did not move, or it is a wall rather than a check.
		var act = () => Token.AssertDataStamp("data-1", "memory_search");

		act.Should().NotThrow();
	}

	static string FingerprintRefusal() =>
		Capture(() => Token.AssertFingerprint("fp-2", "memory_search"));

	static string DataStampRefusal() =>
		Capture(() => Token.AssertDataStamp("data-2", "memory_search"));

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
