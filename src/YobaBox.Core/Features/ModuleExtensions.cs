using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace YobaBox.Core.Features;

public static class ModuleExtensions
{
	public static WebApplicationBuilder AddConfigModule(this WebApplicationBuilder builder)
	{
		if (builder.GetFeatureFlags().IsEnabled("Config"))
		{
			// Phase 1: register ConfigApi endpoints
		}
		return builder;
	}

	public static WebApplicationBuilder AddLogModule(this WebApplicationBuilder builder)
	{
		if (builder.GetFeatureFlags().IsEnabled("Logging"))
		{
			// Phase 2: register KQL ingestion + query
		}
		return builder;
	}

	public static WebApplicationBuilder AddDataModule(this WebApplicationBuilder builder)
	{
		if (builder.GetFeatureFlags().IsEnabled("Data"))
		{
			// Phase 3: register PostgREST API
		}
		return builder;
	}

	public static WebApplicationBuilder AddDashboardModule(this WebApplicationBuilder builder)
	{
		if (builder.GetFeatureFlags().IsEnabled("Dashboard"))
		{
			// Phase 4: register HealthPoller, CiPoller
		}
		return builder;
	}

	public static WebApplication UseConfigModule(this WebApplication app)
	{
		if (app.GetFeatureFlags().IsEnabled("Config"))
		{
			// Phase 1: map Config endpoints
		}
		return app;
	}

	public static WebApplication UseLogModule(this WebApplication app)
	{
		if (app.GetFeatureFlags().IsEnabled("Logging"))
		{
			// Phase 2: map Log endpoints
		}
		return app;
	}

	public static WebApplication UseDataModule(this WebApplication app)
	{
		if (app.GetFeatureFlags().IsEnabled("Data"))
		{
			// Phase 3: map Data endpoints
		}
		return app;
	}

	public static WebApplication UseDashboardModule(this WebApplication app)
	{
		if (app.GetFeatureFlags().IsEnabled("Dashboard"))
		{
			// Phase 4: map Dashboard endpoints
		}
		return app;
	}

	static FeatureFlags GetFeatureFlags(this WebApplicationBuilder builder) =>
		new(builder.Configuration);

	static FeatureFlags GetFeatureFlags(this WebApplication app) =>
		new(app.Configuration);
}
