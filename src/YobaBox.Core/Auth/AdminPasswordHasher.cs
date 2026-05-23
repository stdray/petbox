using System.Security.Cryptography;

namespace YobaBox.Core.Auth;

public static class AdminPasswordHasher
{
	const int Iterations = 100_000;
	const int SaltBytes = 16;
	const int HashBytes = 32;
	const string Prefix = "pbkdf2";

	public static string Hash(string password)
	{
		ArgumentNullException.ThrowIfNull(password);
		var salt = RandomNumberGenerator.GetBytes(SaltBytes);
		var hash = Rfc2898DeriveBytes.Pbkdf2(
			password,
			salt,
			Iterations,
			HashAlgorithmName.SHA256,
			HashBytes);
		return $"{Prefix}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
	}

	public static bool Verify(string password, string encodedHash)
	{
		ArgumentNullException.ThrowIfNull(password);
		if (string.IsNullOrEmpty(encodedHash))
			return false;

		var parts = encodedHash.Split('$');
		if (parts.Length != 4 || parts[0] != Prefix)
			return false;
		if (!int.TryParse(parts[1], out var iterations) || iterations < 1)
			return false;

		byte[] salt;
		byte[] expected;
		try
		{
			salt = Convert.FromBase64String(parts[2]);
			expected = Convert.FromBase64String(parts[3]);
		}
		catch (FormatException)
		{
			return false;
		}

		var actual = Rfc2898DeriveBytes.Pbkdf2(
			password,
			salt,
			iterations,
			HashAlgorithmName.SHA256,
			expected.Length);
		return CryptographicOperations.FixedTimeEquals(actual, expected);
	}
}
