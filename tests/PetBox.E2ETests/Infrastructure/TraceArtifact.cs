using Microsoft.Playwright;

namespace PetBox.E2ETests.Infrastructure;

public static class TraceArtifact
{
	static readonly string ArtifactsDir = Path.Combine(AppContext.BaseDirectory, "artifacts");

	public static async Task StartAsync(IBrowserContext ctx) =>
		await ctx.Tracing.StartAsync(new TracingStartOptions
		{
			Screenshots = true,
			Snapshots = true,
			Sources = true,
		});

	// `output` is kept for source compatibility with the 24 call sites (and because several of
	// them inject ITestOutputHelper for this call alone); v3 no longer needs it to name the trace.
	public static async Task StopAndSaveAsync(IBrowserContext ctx, ITestOutputHelper output)
	{
		_ = output;
		Directory.CreateDirectory(ArtifactsDir);
		var slug = Sanitize(ExtractTestName());
		var path = Path.Combine(ArtifactsDir, slug + ".zip");
		await ctx.Tracing.StopAsync(new TracingStopOptions { Path = path });
	}

	// v2 had no public way to learn the running test's name here, so this reached through
	// reflection into ITestOutputHelper's private `test` field. v3 exposes the ambient test on
	// TestContext, so the hack is gone — this is the supported API.
	static string ExtractTestName() =>
		TestContext.Current.Test?.TestDisplayName ?? "unknown-" + Guid.NewGuid().ToString("N")[..8];

	static string Sanitize(string s)
	{
		var invalid = Path.GetInvalidFileNameChars();
		var chars = s.Select(c => invalid.Contains(c) || c == ' ' ? '_' : c).ToArray();
		var result = new string(chars);
		return result.Length > 120 ? result[..120] : result;
	}
}
