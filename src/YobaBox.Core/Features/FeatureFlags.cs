using Microsoft.Extensions.Configuration;

namespace YobaBox.Core.Features;

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

	public bool IsEnabled(string name) =>
		_flags.TryGetValue(name, out var enabled) && enabled;
}
