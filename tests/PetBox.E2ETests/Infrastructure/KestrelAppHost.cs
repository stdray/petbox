using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Web;

namespace PetBox.E2ETests.Infrastructure;

public sealed class KestrelAppHost : IAsyncDisposable
{
	WebApplication? _app;
	string _tempDir = "";

	public string BaseUrl { get; private set; } = "";
	public string DataDir => _tempDir;
	public IServiceProvider Services => _app?.Services ?? throw new InvalidOperationException("Host not started");

	public async Task StartAsync(Action<IDictionary<string, string?>> configure)
	{
		_tempDir = Path.Combine(Path.GetTempPath(), "petbox-e2e-" + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(_tempDir);

		// A remote CDP browser (PETBOX_E2E_CDP, e.g. lightpanda in WSL) can't reach the
		// Windows loopback — bind all interfaces and advertise a host-reachable address.
		var remoteBrowser = Environment.GetEnvironmentVariable("PETBOX_E2E_CDP") is { Length: > 0 };
		var settings = new Dictionary<string, string?>(
			StringComparer.OrdinalIgnoreCase)
		{
			["ConnectionStrings:PetBox"] = $"Data Source={_tempDir}/petbox.db",
			["urls"] = remoteBrowser ? "http://0.0.0.0:0" : "http://127.0.0.1:0",
		};
		configure(settings);

		var builder = WebApplication.CreateBuilder(new WebApplicationOptions
		{
			EnvironmentName = "Testing",
			ApplicationName = typeof(Program).Assembly.GetName().Name,
			WebRootPath = WebProjectWwwroot(),
		});
		builder.Configuration.AddInMemoryCollection(settings);
		Program.ConfigureServices(builder);

		_app = builder.Build();
		Program.Configure(_app);
		await _app.StartAsync();

		BaseUrl = _app.Services.GetRequiredService<IServer>()
			.Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault()
			?? throw new InvalidOperationException("Kestrel did not report an address");

		if (remoteBrowser)
		{
			// PETBOX_E2E_BASEHOST overrides; default = first non-loopback IPv4 of this machine.
			var host = Environment.GetEnvironmentVariable("PETBOX_E2E_BASEHOST")
				?? System.Net.Dns.GetHostAddresses(System.Net.Dns.GetHostName())
					.First(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
						&& !System.Net.IPAddress.IsLoopback(a)).ToString();
			BaseUrl = BaseUrl.Replace("0.0.0.0", host);
		}

		WarmUp();
	}

	void WarmUp()
	{
		using var client = new HttpClient { BaseAddress = new Uri(BaseUrl) };
		for (var i = 0; i < 30; i++)
		{
			try
			{
				var res = client.GetAsync("/health").GetAwaiter().GetResult();
				if (res.IsSuccessStatusCode) break;
			}
			catch (HttpRequestException) { /* keep polling */ }
			Thread.Sleep(100);
		}
	}

	static string WebProjectWwwroot()
	{
		var attr = typeof(KestrelAppHost).Assembly
			.GetCustomAttributes<AssemblyMetadataAttribute>()
			.FirstOrDefault(a => a.Key == "PetBoxWebProjectDir")
			?? throw new InvalidOperationException(
				"AssemblyMetadataAttribute('PetBoxWebProjectDir') missing — check PetBox.E2ETests.csproj.");
		return Path.Combine(attr.Value!, "wwwroot");
	}

	public async ValueTask DisposeAsync()
	{
		if (_app is not null)
		{
			await _app.StopAsync();
			await _app.DisposeAsync();
		}
		try { Directory.Delete(_tempDir, recursive: true); }
		catch { /* best-effort */ }
	}
}
