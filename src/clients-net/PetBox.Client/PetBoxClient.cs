namespace PetBox.Client;

// Entry point for the PetBox .NET SDK. Owns an authenticated HttpClient (built via the shared
// PetBoxTransport) and exposes the typed per-surface clients. Today: Data. Config has its own
// specialized provider package (PetBox.Client.Config) built on the same transport.
//
//     using var client = new PetBoxClient(new PetBoxClientOptions
//     {
//         Endpoint = "https://petbox.3po.su",
//         ApiKey = Environment.GetEnvironmentVariable("PETBOX_API_KEY")!,
//     });
//     var rows = await client.Data.QueryAsync("kpvotes", "cache", "SELECT * FROM votes");
public sealed class PetBoxClient : IDisposable
{
	readonly HttpClient _http;

	public PetBoxClient(PetBoxClientOptions options)
	{
		ArgumentNullException.ThrowIfNull(options);
		_http = PetBoxTransport.CreateHttpClient(options.Endpoint, options.ApiKey, options.Handler);
		Data = new PetBoxDataClient(_http);
	}

	// Typed client for the Data module (raw-SQL pass-through + DataDb provisioning).
	public PetBoxDataClient Data { get; }

	public void Dispose() => _http.Dispose();
}
