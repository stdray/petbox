using LinqToDB;
using LinqToDB.Data;

namespace PetBox.Deploy.Data;

// Hands out FRESH, caller-owned connections to the fleet-wide deploy db (data/deploy.db). The
// caller disposes. Same contract, and the same reason, as ICoreDbFactory: a linq2db DataConnection
// is not thread-safe, so no connection may outlive a single call and be reachable from two threads.
//
// DeployDb is no longer registered in DI at all — this factory is the ONLY way to get one, which is
// what makes "a scoped connection used from several threads" unrepresentable rather than merely
// absent. DeployDbInjectionGuardTests holds that line.
public interface IDeployDbFactory
{
	// A fresh, caller-owned connection to deploy.db. Never share it across threads; dispose it.
	DeployDb Open();
}

// Registered as a SINGLETON: it holds no connection, only the immutable DataOptions describing how
// to make one. Schema is ensured once at startup by DeploySchema.Ensure, so Open() does no DDL.
public sealed class DeployDbFactory : IDeployDbFactory
{
	readonly DataOptions<DeployDb> _options;

	public DeployDbFactory(string connectionString)
		: this(DeployDb.CreateOptions(connectionString)) { }

	// Clones the supplied options — preserves the provider, the connection string and the mapping.
	public DeployDbFactory(DataOptions<DeployDb> options) =>
		_options = new DataOptions<DeployDb>(options.Options);

	public DeployDb Open() => new(_options);
}
