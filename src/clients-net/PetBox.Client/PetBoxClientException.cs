using System.Net;

namespace PetBox.Client;

// Thrown when a PetBox API call returns a non-success status. Carries the status code and
// the raw response body (petbox error endpoints return `{ "error": "..." }`) so callers can
// branch on, e.g., 404 (DataDb not found), 409 (conflict), 403 (scope), 507 (quota).
public sealed class PetBoxClientException : Exception
{
	public HttpStatusCode StatusCode { get; }
	public string? ResponseBody { get; }

	public PetBoxClientException(HttpStatusCode statusCode, string? responseBody, string message)
		: base(message)
	{
		StatusCode = statusCode;
		ResponseBody = responseBody;
	}
}
