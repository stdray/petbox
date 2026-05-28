using FluentMigrator.Runner;
using Microsoft.Extensions.DependencyInjection;

namespace PetBox.Core.Data;

public static class MigrationRunner
{
	public static void Run(string connectionString)
	{
		var services = new ServiceCollection()
			.AddFluentMigratorCore()
			.ConfigureRunner(rb => rb
				.AddSQLite()
				.WithGlobalConnectionString(connectionString)
				.ScanIn(typeof(Migrations.M001_Initial).Assembly).For.Migrations())
			.BuildServiceProvider();

		using var scope = services.CreateScope();
		var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
		runner.MigrateUp();
	}
}
