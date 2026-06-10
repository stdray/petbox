using PetBox.Core.Data;

namespace PetBox.Deploy.Data;

// Schema bootstrap for the single fleet-wide deploy db. Applies the Core invariants
// (WAL + busy_timeout) then runs the Deploy FluentMigrator set. Idempotent; called once
// at startup. DDL lives in Migrations/.
public static class DeploySchema
{
	public static void Ensure(string connectionString)
	{
		SqlitePragmas.ApplyWal(connectionString);
		MigrationRunner.Run(connectionString, typeof(Migrations.M001_Deploy).Assembly);
	}
}
