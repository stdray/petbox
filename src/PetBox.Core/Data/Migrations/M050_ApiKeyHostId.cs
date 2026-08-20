using FluentMigrator;

namespace PetBox.Core.Data.Migrations;

// spec node-grant-own-carrier, via work `node-key-own-carrier`: a grant limited to ONE MACHINE gets
// a carrier of its own type. The node-agent key used to park the node id in ApiKeys.ProjectKey — the
// column that names a TENANT — so a node `vdsina-1` and a project `vdsina-1` were the same value in
// the same column, read through the same `project` claim. That is a NAME COLLISION in the schema,
// not a convention anyone could tighten: no naming rule can separate two things that share a field.
//
// BREAKING BY CHOICE, no read-side fallback. The alternative — read HostId, fall back to ProjectKey
// when it is null — would have kept the collision alive (a project-scoped key whose ProjectKey
// happens to match a node id would still resolve as that node) and would have carried a permanent
// branch in the auth path for the sake of ONE row. Live data is a single ephemeral test node
// (`local-pc`, zero deployments), so the whole compatibility burden here is one UPDATE.
//
// The predicate is the node key's own addressing convention, not a guess: BOTH mint paths
// (DeployAgentService.EnrollNodeAsync and AgentKeyAdminService.MintNodeKeyAsync) name the row
// `node:<lowercased id>` and generate the secret as `yb_key_node_<guid>`. Requiring both makes a
// false positive on some unrelated operator-named key impossible.
//
// ProjectKey is cleared to '' in the same statement rather than left behind: leaving the node id in
// a tenant column would keep the ambiguity readable in the data even after the code stopped reading
// it, and ProjectScope treats a blank claim as authorizing NOTHING — which is the correct reach for
// a key that is not a tenant's.
[Migration(50, "Add ApiKeys.HostId and move node ids off ProjectKey onto it")]
public sealed class M050_ApiKeyHostId : Migration
{
	const string NodeKeyPredicate =
		"Name LIKE 'node:%' AND Key LIKE 'yb_key_node_%'";

	public override void Up()
	{
		Create.Column("HostId").OnTable("ApiKeys").AsString(100).Nullable();

		// The one-row move. Guarded on HostId IS NULL so a re-run cannot overwrite a host already
		// set, and on ProjectKey <> '' so a row already migrated is not re-blanked into nonsense.
		Execute.Sql(
			"UPDATE ApiKeys SET HostId = ProjectKey, ProjectKey = '' " +
			$"WHERE {NodeKeyPredicate} AND HostId IS NULL AND ProjectKey <> '';");
	}

	// Faithful reverse: put the host id back where the pre-M050 code reads it, then drop the column.
	// Only rows this migration could have produced are touched (a node-named key that HAS a host and
	// has the blank ProjectKey we left it with), so a hand-edited row survives a down unchanged.
	public override void Down()
	{
		Execute.Sql(
			"UPDATE ApiKeys SET ProjectKey = HostId " +
			$"WHERE {NodeKeyPredicate} AND HostId IS NOT NULL AND ProjectKey = '';");

		Delete.Column("HostId").FromTable("ApiKeys");
	}
}
