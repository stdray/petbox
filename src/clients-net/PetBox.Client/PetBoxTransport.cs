namespace PetBox.Client;

// The shared core transport: builds an HttpClient authenticated for PetBox. This is the
// single place auth + base-address wiring lives, reused by every typed client (Data here,
// and the config provider in PetBox.Client.Config) so the transport isn't duplicated.
public static class PetBoxTransport
{
	// Canonical PetBox auth header. The server also accepts the legacy X-YobaConf-ApiKey
	// and `Authorization: Bearer/Token`, but new clients send X-Api-Key.
	public const string ApiKeyHeader = "X-Api-Key";

	// Builds an HttpClient with BaseAddress set to `endpoint` (trailing slash normalised) and
	// the X-Api-Key header applied to every request. `handler` is an escape hatch for tests;
	// when supplied it is NOT disposed by the returned client (the caller owns it).
	public static HttpClient CreateHttpClient(string endpoint, string apiKey, HttpMessageHandler? handler = null)
	{
		if (string.IsNullOrWhiteSpace(endpoint))
			throw new ArgumentException("endpoint is required.", nameof(endpoint));
		if (string.IsNullOrWhiteSpace(apiKey))
			throw new ArgumentException("apiKey is required.", nameof(apiKey));

		var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
		var baseUrl = endpoint.EndsWith('/') ? endpoint : endpoint + "/";
		http.BaseAddress = new Uri(baseUrl);
		http.DefaultRequestHeaders.TryAddWithoutValidation(ApiKeyHeader, apiKey);
		return http;
	}
}
