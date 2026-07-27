using PetBox.Core.Search;

namespace PetBox.Tests.SearchCore;

public sealed class VectorMathTests
{
	[Fact]
	public void Cosine_IdenticalVectors_IsOne()
	{
		var v = new[] { 1f, 2f, 3f };
		// Float32 (SIMD TensorPrimitives) precision: ~1e-7 is the honest tolerance —
		// cosine feeds RANK fusion, where that error is far below any score gap.
		VectorMath.Cosine(v, v).Should().BeApproximately(1.0, 1e-6);
	}

	[Fact]
	public void Cosine_OrthogonalVectors_IsZero()
	{
		VectorMath.Cosine(new[] { 1f, 0f }, new[] { 0f, 1f }).Should().BeApproximately(0.0, 1e-9);
	}

	[Fact]
	public void Cosine_LengthMismatch_IsZero()
	{
		VectorMath.Cosine(new[] { 1f, 2f, 3f }, new[] { 1f, 2f }).Should().Be(0);
	}

	[Fact]
	public void TopK_OrdersByCosineDescending()
	{
		var query = new[] { 1f, 0f };
		var candidates = new (string, float[])[]
		{
			("orthogonal", new[] { 0f, 1f }),   // cosine 0
			("identical", new[] { 1f, 0f }),    // cosine 1
			("close", new[] { 1f, 0.1f }),      // cosine ~0.995
		};
		var top = VectorMath.TopK(query, candidates, 3);
		top.Select(t => t.Key).Should().Equal("identical", "close", "orthogonal");
	}

	[Fact]
	public void TopK_SkipsLengthMismatchedCandidates()
	{
		var query = new[] { 1f, 0f };
		var candidates = new (string, float[])[]
		{
			("good", new[] { 1f, 0f }),
			("wrongDim", new[] { 1f, 0f, 0f }),  // different length → skipped
		};
		var top = VectorMath.TopK(query, candidates, 10);
		top.Select(t => t.Key).Should().Equal("good");
	}

	[Fact]
	public void TopK_RespectsK()
	{
		var query = new[] { 1f, 0f };
		var candidates = Enumerable.Range(0, 5)
			.Select(i => (i.ToString(), new[] { 1f, i * 0.1f }))
			.ToArray();
		VectorMath.TopK(query, candidates, 2).Should().HaveCount(2);
	}

	// search-legs-tie-break-nondeterministic: OrderByDescending(Score) alone is stable only
	// relative to `candidates`' own enumeration order — and that order comes from a caller's SQL
	// scan with no ORDER BY (VectorSearchIndex.SearchAsync), which is not guaranteed stable across
	// pool rebuilds. Same equal-scoring candidate SET, fed in two different orders (standing in
	// for two independent rebuilds), must sort identically. Reverting the ThenBy(Key, Ordinal)
	// fix reproduces the divergence directly: without it TopK just echoes each call's input
	// order verbatim (LINQ OrderBy is a stable sort), so feeding the reverse permutation is
	// GUARANTEED to reverse the tied output too — a reliable, non-flaky red.
	[Fact]
	public void TopK_TiesBrokenByKeyOrdinal_RegardlessOfCandidateOrder()
	{
		var query = new[] { 1f, 0f };
		var equalScoring = new (string, float[])[]
		{
			("zzz", new[] { 1f, 0f }),
			("mmm", new[] { 1f, 0f }),
			("aaa", new[] { 1f, 0f }),
		};

		var topForward = VectorMath.TopK(query, equalScoring, 10);
		var topReversed = VectorMath.TopK(query, equalScoring.Reverse().ToArray(), 10);

		var expected = new[] { "aaa", "mmm", "zzz" }; // ordinal key order
		topForward.Select(t => t.Key).Should().Equal(expected);
		topReversed.Select(t => t.Key).Should().Equal(expected);
	}
}
