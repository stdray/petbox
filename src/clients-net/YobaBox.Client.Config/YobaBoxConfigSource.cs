using Microsoft.Extensions.Configuration;

namespace YobaBox.Client.Config;

// Factory for YobaBoxConfigProvider. ConfigurationBuilder.Add(source) calls
// Build(builder) once per Build() of the root IConfiguration. We don't need build-time
// state — just hand the provider the already-populated options.
public sealed class YobaBoxConfigSource : IConfigurationSource
{
	public YobaBoxConfigOptions Options { get; init; } = new();

	public IConfigurationProvider Build(IConfigurationBuilder builder) =>
		new YobaBoxConfigProvider(Options);
}
