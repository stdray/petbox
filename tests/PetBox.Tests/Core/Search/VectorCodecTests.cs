using PetBox.Core.Search;

namespace PetBox.Tests.SearchCore;

public sealed class VectorCodecTests
{
	[Fact]
	public void Encode_Decode_RoundTripsExactly()
	{
		var v = new[] { 0f, 1f, -1f, 3.1415927f, float.MaxValue, float.MinValue, 0.0001f };
		var back = VectorCodec.Decode(VectorCodec.Encode(v));
		back.Should().Equal(v);
	}

	[Fact]
	public void Encode_ProducesFourBytesPerFloat()
	{
		VectorCodec.Encode(new[] { 1f, 2f, 3f }).Length.Should().Be(12);
	}

	[Fact]
	public void Encode_EmptyVector_RoundTrips()
	{
		VectorCodec.Decode(VectorCodec.Encode([])).Should().BeEmpty();
	}
}
