namespace PetBox.Client;

// Caller-provided knobs for a PetBoxClient. The API key is project-scoped on the server
// (its `project` claim authorizes the {projectKey} in Data URLs), so one client typically
// targets one project's surfaces.
//
// `Handler` is an escape hatch for tests — inject WebApplicationFactory's
// Server.CreateHandler() so the SDK talks to an in-process petbox without touching the
// network. Production leaves it null and the client builds its own HttpClient.
public sealed class PetBoxClientOptions
{
	// e.g. "https://petbox.3po.su". Trailing slash optional — normalised internally.
	public string Endpoint { get; set; } = string.Empty;

	// Plaintext token. Sent as the `X-Api-Key` header on every request.
	public string ApiKey { get; set; } = string.Empty;

	// Testing-only: inject a custom HttpMessageHandler (e.g. WebApplicationFactory's
	// TestServer handler). Production leaves this null.
	public HttpMessageHandler? Handler { get; set; }
}
