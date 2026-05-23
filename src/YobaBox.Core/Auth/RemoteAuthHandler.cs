using Microsoft.Extensions.Logging;

namespace YobaBox.Core.Auth;

public sealed class RemoteAuthHandler
{
	readonly HttpClient _http;
	readonly string _remoteUrl;
	readonly string? _apiKey;
	readonly ILogger<RemoteAuthHandler> _logger;

	public RemoteAuthHandler(HttpClient http, AuthConfiguration config, ILogger<RemoteAuthHandler> logger)
	{
		_http = http;
		_remoteUrl = config.RemoteUrl!;
		_apiKey = config.RemoteApiKey;
		_logger = logger;
	}

	public async Task<RemoteValidationResult> ValidateAsync(string apiKey, CancellationToken ct)
	{
		try
		{
			var request = new HttpRequestMessage(HttpMethod.Get,
				$"{_remoteUrl.TrimEnd('/')}/api/auth/validate");
			request.Headers.Add("X-Api-Key", apiKey);

			if (_apiKey is not null)
				request.Headers.Add("X-Auth-Api-Key", _apiKey);

			var response = await _http.SendAsync(request, ct);

			if (response.IsSuccessStatusCode)
			{
				return RemoteValidationResult.Valid;
			}

			if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
				return RemoteValidationResult.Invalid;

			_logger.LogWarning("Remote auth returned unexpected status {Status}", response.StatusCode);
			return RemoteValidationResult.Invalid;
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Remote auth request failed");
			return RemoteValidationResult.Error;
		}
	}
}

public enum RemoteValidationResult { Valid, Invalid, Error }
