using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using YobaBox.Web;

namespace YobaBox.E2ETests.Infrastructure;

public sealed class KestrelAppHost : IAsyncDisposable
{
	WebApplication? _app;
	string _tempDir = "";

	public string BaseUrl { get; private set; } = "";
	public string DataDir => _tempDir;
	public IServiceProvider Services => _app?.Services ?? throw new InvalidOperationException("Host not started");

	public async Task StartAsync(Action<IDictionary<string, string?>> configure)
	{
		_tempDir = Path.Combine(Path.GetTempPath(), "yobabox-e2e-" + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(_tempDir);

		var settings = new Dictionary<string, string?>(
			StringComparer.OrdinalIgnoreCase)
		{
			["ConnectionStrings:YobaBox"] = $"Data Source={_tempDir}/yobabox.db",
			["urls"] = "http://127.0.0.1:0",
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
			.FirstOrDefault(a => a.Key == "YobaBoxWebProjectDir")
			?? throw new InvalidOperationException(
				"AssemblyMetadataAttribute('YobaBoxWebProjectDir') missing — check YobaBox.E2ETests.csproj.");
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
