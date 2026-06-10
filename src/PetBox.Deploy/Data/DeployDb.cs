using LinqToDB;
using LinqToDB.Data;
using LinqToDB.DataProvider.SQLite;

namespace PetBox.Deploy.Data;

// linq2db context over the single fleet-wide deploy db (data/deploy.db). Unlike the
// per-project temporal stores (Tasks/Memory/Sessions), deploy state is fleet-wide and
// mutable: one node hosts containers from many projects, so there is one DeployDb.
public sealed class DeployDb : DataConnection
{
	public DeployDb(DataOptions<DeployDb> options) : base(options.Options) { }

	public ITable<Node> Nodes => this.GetTable<Node>();
	public ITable<Deployment> Deployments => this.GetTable<Deployment>();
	public ITable<DeploymentStatus> Statuses => this.GetTable<DeploymentStatus>();

	public static DataOptions<DeployDb> CreateOptions(string connectionString) =>
		new(new DataOptions().UseSQLite(connectionString));
}
