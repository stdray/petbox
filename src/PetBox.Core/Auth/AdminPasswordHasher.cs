using System.Security.Cryptography;

namespace PetBox.Core.Auth;

public static class AdminPasswordHasher
{
	const int Iterations = 100_000;
	const int SaltBytes = 16;
	const int HashBytes = 32;
	const string Prefix = "pbkdf2";

	// Test seam for auth-hash-cost-test-is-a-wallclock-flake-in-the-gate: a monotonic call
	// counter, not a wall-clock measurement. AddMemberCompositeFixTests used to assert "the
	// taken-name path pays for the hash" by comparing two Stopwatch readings, which flaked
	// whenever the CI machine ran this test suite alongside PetBox.E2ETests (a separate,
	// concurrent PROCESS — not something an xUnit collection attribute can serialize against)
	// and CPU contention skewed one measurement but not the other. "The hash was actually
	// computed" is what the test cares about, and a call count says that directly.
	//
	// Plain shared counter, deliberately NOT AsyncLocal<int>: verified empirically that an
	// AsyncLocal write made inside an awaited nested async call (Hash() is called from inside
	// AddMemberAsync, which the test awaits) does not flow back to the caller once the callee's
	// async state machine suspends even once — so a test reading AsyncLocal.Value after
	// `await AddMemberAsync(...)` would always see it unchanged, regardless of how many times
	// Hash() actually ran. A plain static counter has none of that; the tradeoff is that it is
	// process-global, so a concurrent, unrelated test's OWN Hash() call can add to a delta
	// measured around one call here. That is harmless: it can only ever push the delta UP, never
	// down, so a caller asserting the delta is >= 1 (never == 1) is immune to that noise while
	// still catching a hash that was skipped entirely — the exact regression this seam guards.
	static long _hashCallCount;

	internal static long HashCallCount => Interlocked.Read(ref _hashCallCount);

	public static string Hash(string password)
	{
		ArgumentNullException.ThrowIfNull(password);
		Interlocked.Increment(ref _hashCallCount);
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
