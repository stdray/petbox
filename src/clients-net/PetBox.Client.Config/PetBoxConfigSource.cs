using Microsoft.Extensions.Configuration;

namespace PetBox.Client.Config;

// Factory for PetBoxConfigProvider. ConfigurationBuilder.Add(source) calls
// Build(builder) once per Build() of the root IConfiguration. We don't need build-time
// state — just hand the provider the already-populated options.
public sealed class PetBoxConfigSource : IConfigurationSource
{
	public PetBoxConfigOptions Options { get; init; } = new();

	public IConfigurationProvider Build(IConfigurationBuilder builder) =>
		new PetBoxConfigProvider(Options);
}
