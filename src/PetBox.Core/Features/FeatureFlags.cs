using Microsoft.Extensions.Configuration;

namespace PetBox.Core.Features;

public enum Feature
{
	Config,
	Logging,
	Data,
	Dashboard,
	Tasks,
	Memory,
	LlmRouter,
}

public sealed class FeatureFlags
{
	readonly Dictionary<string, bool> _flags;

	public FeatureFlags(IConfiguration configuration)
	{
		var section = configuration.GetSection("Features");
		_flags = section.GetChildren()
			.ToDictionary(c => c.Key, c => bool.TryParse(c.Value, out var v) && v,
				StringComparer.OrdinalIgnoreCase);
	}

	public bool IsEnabled(Feature feature) =>
		_flags.TryGetValue(feature.ToString(), out var enabled) && enabled;
}
