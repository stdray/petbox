using Microsoft.Extensions.Configuration;

namespace YobaBox.Client.Config;

// Public entry point for consumers. Usage (in Program.cs):
//
//     builder.Configuration.AddYobaBoxConfig(o =>
//     {
//         o.BaseUrl = "https://yobabox.3po.su";
//         o.ApiKey = Environment.GetEnvironmentVariable("YOBABOX_API_KEY")!;
//         o.WithTag("env", "prod")
//          .WithTag("project", "kpvotes")
//          .WithTag("host", Environment.MachineName);
//         o.RefreshInterval = TimeSpan.FromMinutes(5);
//     });
//
// After this, IConfiguration reads (typed GetValue<T> / options-binding) come from
// the resolved YobaBox JSON. Changes poll-in every RefreshInterval via ETag conditional GET.
public static class YobaBoxConfigExtensions
{
	public static IConfigurationBuilder AddYobaBoxConfig(
		this IConfigurationBuilder builder,
		Action<YobaBoxConfigOptions> configure)
	{
		ArgumentNullException.ThrowIfNull(builder);
		ArgumentNullException.ThrowIfNull(configure);

		var options = new YobaBoxConfigOptions();
		configure(options);

		builder.Add(new YobaBoxConfigSource { Options = options });
		return builder;
	}
}
