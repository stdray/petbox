using PetBox.Core.Contract;

namespace PetBox.Tests.Fragments;

// The pure substitution primitive behind `fragment` on tasks_upsert / comments_upsert /
// memory_upsert (work/write-fragment-patch, spec/write-cost-follows-change). The three services
// each resolve a fragment against the row their own read-merge is holding; the ARITHMETIC of what
// counts as a match, and what must be refused, lives here and is tested here once.
//
// The load-bearing property is that ambiguity and absence are REFUSALS, not guesses: a caller
// cannot see which occurrence a "first match" rule would have picked, so silently picking one
// edits text the caller never looked at.
public sealed class FragmentPatchPrimitiveTests
{
	static FragmentEdit E(string? old, string? @new) => new(old, @new);

	// ── the happy path (the CONTROL: without this, every refusal test below could pass
	//    simply because the primitive refuses everything) ──────────────────────────────

	[Fact]
	public void UniqueMatch_IsReplaced_AndTheRestOfTheBodyIsUntouched()
	{
		var r = FragmentPatch.Apply("alpha BETA gamma", [E("BETA", "delta")]);

		r.Ok.Should().BeTrue();
		r.Body.Should().Be("alpha delta gamma");
		r.Error.Should().BeNull();
	}

	[Fact]
	public void EmptyNew_DeletesTheMatchedText()
	{
		// "" is the sanctioned way to delete — which is exactly why a MISSING `new` must not
		// also mean deletion (see NullNew_IsRefused below).
		var r = FragmentPatch.Apply("keep [drop this] keep", [E(" [drop this]", "")]);

		r.Ok.Should().BeTrue();
		r.Body.Should().Be("keep keep");
	}

	// ── refusals ─────────────────────────────────────────────────────────────────────

	[Fact]
	public void TwoMatches_IsRefused_WithTheCount_AndNoBody()
	{
		var r = FragmentPatch.Apply("x TARGET y TARGET z", [E("TARGET", "Z")]);

		r.Ok.Should().BeFalse();
		r.Body.Should().BeEmpty();                       // never a partially-resolved body
		r.Error.Should().Contain("occurs 2 times").And.Contain("EXACTLY once");
	}

	[Fact]
	public void TheReportedMatchCount_IsTheRealOne_NotCappedAtTwo()
	{
		// The message quotes a specific number, so that number has to be true: an early-exit
		// counter that stops at 2 would tell a caller with five matches it has two, and the
		// caller would extend its fragment once and fail again for a reason it was mis-told.
		var r = FragmentPatch.Apply(string.Concat(Enumerable.Repeat("a X b ", 5)), [E("X", "Y")]);

		r.Ok.Should().BeFalse();
		r.Error.Should().Contain("occurs 5 times");
	}

	[Fact]
	public void ZeroMatches_IsRefused_AndSaysTheTextMoved()
	{
		var r = FragmentPatch.Apply("the current text", [E("not in here", "z")]);

		r.Ok.Should().BeFalse();
		r.Error.Should().Contain("does not occur").And.Contain("re-read");
	}

	[Fact]
	public void EmptyEditList_IsRefused()
	{
		FragmentPatch.Apply("body", []).Ok.Should().BeFalse();
		FragmentPatch.Apply("body", null).Ok.Should().BeFalse();
	}

	[Fact]
	public void EmptyOld_IsRefused()
	{
		// "" occurs everywhere; treating it as a match would splice text at an arbitrary offset.
		FragmentPatch.Apply("body", [E("", "x")]).Error.Should().Contain("'old' is required");
		FragmentPatch.Apply("body", [E(null, "x")]).Error.Should().Contain("'old' is required");
	}

	[Fact]
	public void NullNew_IsRefused_BecauseAMisspeltFieldMustNotDeleteText()
	{
		// McpUnknownParameterFilter walks the top level and ONE hop into batch items, so it does
		// NOT police the fields inside `fragment[]`. A caller who writes {old:"x", nw:"y"} lands
		// here with New == null. If null meant "", that typo would DELETE "x" and report success.
		var r = FragmentPatch.Apply("has x in it", [E("x", null)]);

		r.Ok.Should().BeFalse();
		r.Error.Should().Contain("'new' is required").And.Contain("\"\"");
	}

	// ── the list: in order, and all-or-none ──────────────────────────────────────────

	[Fact]
	public void Edits_ApplyInOrder_EachSeeingThePreviousResult()
	{
		var r = FragmentPatch.Apply("one two three", [E("one", "1"), E("1 two", "1 2"), E("three", "3")]);

		r.Ok.Should().BeTrue();
		r.Body.Should().Be("1 2 3");
	}

	[Fact]
	public void OneBadEditInTheList_RefusesTheWholeList_NotAPrefixOfIt()
	{
		// Edit #0 and #1 are individually fine; #2 matches nothing. The verdict is a refusal with
		// NO body — the caller must never receive a two-thirds-applied text.
		var r = FragmentPatch.Apply("a b c", [E("a", "A"), E("b", "B"), E("zzz", "Z")]);

		r.Ok.Should().BeFalse();
		r.Body.Should().BeEmpty();
		r.Error.Should().Contain("fragment[2]").And.Contain("does not occur");
	}

	[Fact]
	public void AnEditMadeAmbiguousByAnEarlierEdit_IsRefused()
	{
		// Order matters for uniqueness too: after #0 turns "b" into "a", "a" occurs twice, so #1
		// is ambiguous even though it was unique in the ORIGINAL text. Refusing is correct — the
		// running buffer is the text the edit actually applies to.
		var r = FragmentPatch.Apply("a b", [E("b", "a"), E("a", "c")]);

		r.Ok.Should().BeFalse();
		r.Error.Should().Contain("fragment[1]").And.Contain("occurs 2 times");
	}

	[Fact]
	public void SingleEditList_NamesTheFieldWithoutAnIndex()
	{
		// A one-element list has no "which one" question, so the message stays 'fragment', not
		// 'fragment[0]' — the index is only noise when there is nothing to disambiguate.
		var r = FragmentPatch.Apply("body", [E("nope", "x")]);

		r.Error.Should().Contain("'fragment':").And.NotContain("[0]");
	}

	[Fact]
	public void Matching_IsOrdinal_NotCultureOrCaseFolded()
	{
		// The caller copied `old` out of a body it read; two strings it can see are different
		// must never be equated.
		FragmentPatch.Apply("Straße", [E("STRASSE", "x")]).Ok.Should().BeFalse();
		FragmentPatch.Apply("abc", [E("ABC", "x")]).Ok.Should().BeFalse();
	}

	[Fact]
	public void OverlappingOccurrences_AreCountedNonOverlapping()
	{
		// "aaaa" holds TWO non-overlapping "aa". Counting the three overlapping positions would
		// refuse edits that are genuinely unique as slices of text.
		FragmentPatch.Apply("aaaa", [E("aa", "b")]).Error.Should().Contain("occurs 2 times");
		FragmentPatch.Apply("aaa", [E("aa", "b")]).Ok.Should().BeTrue(); // one non-overlapping match
	}
}
