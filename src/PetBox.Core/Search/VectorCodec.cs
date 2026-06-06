using System.Buffers.Binary;

namespace PetBox.Core.Search;

// Pack/unpack a float[] embedding as a little-endian float32 BLOB for SQLite storage.
// Explicit little-endian (not raw Buffer.BlockCopy) so the encoding is portable and
// independent of machine endianness. Round-trip is exact.
public static class VectorCodec
{
	public static byte[] Encode(float[] v)
	{
		var bytes = new byte[v.Length * sizeof(float)];
		for (var i = 0; i < v.Length; i++)
			BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * sizeof(float), sizeof(float)), v[i]);
		return bytes;
	}

	public static float[] Decode(byte[] b)
	{
		var v = new float[b.Length / sizeof(float)];
		for (var i = 0; i < v.Length; i++)
			v[i] = BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(i * sizeof(float), sizeof(float)));
		return v;
	}
}
