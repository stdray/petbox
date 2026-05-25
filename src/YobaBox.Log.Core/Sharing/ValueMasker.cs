using System.Security.Cryptography;
using System.Text;

namespace YobaBox.Log.Core.Sharing;

public sealed class ValueMasker
{
	readonly byte[] _salt;

	public ValueMasker(ReadOnlySpan<byte> salt) => _salt = salt.ToArray();

	public string Mask(string path, string? value)
	{
		if (string.IsNullOrEmpty(value))
			return "";
		var prefix = PrefixFor(path);
		Span<byte> hash = stackalloc byte[32];
		HMACSHA256.HashData(_salt, Encoding.UTF8.GetBytes(value), hash);
		return prefix + Convert.ToHexString(hash[..4]).ToLowerInvariant();
	}

	static string PrefixFor(string path)
	{
		var lastDot = path.LastIndexOf('.');
		var segment = lastDot < 0 ? path : path[(lastDot + 1)..];
		return segment.Length == 0 ? "masked:" : segment.ToLowerInvariant() + ":";
	}
}
