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
}
