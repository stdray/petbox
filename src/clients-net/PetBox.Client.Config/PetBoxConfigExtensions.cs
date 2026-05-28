using Microsoft.Extensions.Configuration;

namespace PetBox.Client.Config;

// Public entry point for consumers. Usage (in Program.cs):
//
//     builder.Configuration.AddPetBoxConfig(o =>
//     {
//         o.BaseUrl = "https://petbox.3po.su";
//         o.ApiKey = Environment.GetEnvironmentVariable("PETBOX_API_KEY")!;
//         o.WithTag("env", "prod")
//          .WithTag("project", "kpvotes")
//          .WithTag("host", Environment.MachineName);
//         o.RefreshInterval = TimeSpan.FromMinutes(5);
//     });
//
// After this, IConfiguration reads (typed GetValue<T> / options-binding) come from
// the resolved PetBox JSON. Changes poll-in every RefreshInterval via ETag conditional GET.
public static class PetBoxConfigExtensions
{
	public static IConfigurationBuilder AddPetBoxConfig(
		this IConfigurationBuilder builder,
		Action<PetBoxConfigOptions> configure)
	{
		ArgumentNullException.ThrowIfNull(builder);
		ArgumentNullException.ThrowIfNull(configure);

		var options = new PetBoxConfigOptions();
		configure(options);

		builder.Add(new PetBoxConfigSource { Options = options });
		return builder;
	}
}
