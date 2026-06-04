using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PetBox.LlmRouter.Contract;

namespace PetBox.LlmRouter.Http;

// Builds and caches one HttpClient per endpoint (llm-endpoint-security + llm-fast-down):
//   - a short ConnectTimeout so an unreachable endpoint fails fast (not a 100s hang);
//   - SHA-256 certificate pinning when the endpoint declares a CertThumbprint, so a
//     self-signed home endpoint is trusted exactly (and only) by its fingerprint — works
//     from the Linux/Docker container where the cert isn't in any trust store.
// Handlers are expensive, so clients are cached by endpoint identity. Singleton + IDisposable.
public sealed class CertPinningHttpClientProvider : IDisposable
{
	readonly ConcurrentDictionary<string, HttpClient> _clients = new(StringComparer.Ordinal);

	public HttpClient Get(LlmEndpoint endpoint) =>
		_clients.GetOrAdd(CacheKey(endpoint), _ => Build(endpoint));

	static string CacheKey(LlmEndpoint e) =>
		$"{e.Name}|{e.BaseUrl}|{e.CertThumbprint}|{e.ConnectTimeoutMs}|{e.RequestTimeoutMs}";

	static HttpClient Build(LlmEndpoint e)
	{
		var handler = new SocketsHttpHandler
		{
			ConnectTimeout = TimeSpan.FromMilliseconds(e.ConnectTimeoutMs),
			PooledConnectionLifetime = TimeSpan.FromMinutes(5),
		};

		var pin = Normalize(e.CertThumbprint);
		if (pin is not null)
		{
			handler.SslOptions.RemoteCertificateValidationCallback = (_, cert, _, _) =>
			{
				if (cert is null) return false;
				var actual = Convert.ToHexString(cert.GetCertHash(HashAlgorithmName.SHA256));
				return string.Equals(actual, pin, StringComparison.OrdinalIgnoreCase);
			};
		}

		return new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(e.RequestTimeoutMs) };
	}

	// Accept thumbprints with colons/spaces; compare as upper-hex, case-insensitively.
	static string? Normalize(string? thumbprint)
	{
		if (string.IsNullOrWhiteSpace(thumbprint)) return null;
		return thumbprint.Replace(":", "").Replace(" ", "").Trim().ToUpperInvariant();
	}

	public void Dispose()
	{
		foreach (var c in _clients.Values) c.Dispose();
		_clients.Clear();
	}
}
