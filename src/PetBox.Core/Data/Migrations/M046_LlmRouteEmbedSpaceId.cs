using FluentMigrator;

namespace PetBox.Core.Data.Migrations;

// llm-embed-space-id: the vector index must be keyed by a CANONICAL embedding-space name, not by
// the provider's model string. Until now llm_routes.Model was both — the API parameter sent to the
// provider AND the key rows are stored/searched under (VectorSearchIndex.Row.Model). That coupling
// makes a second embed provider impossible: an OpenRouter fallback whose model is "qwen/qwen3-embedding-4b"
// would split the index away from the home route's "qwen3-embed-4b", even though the vectors are the
// same space.
//
// EmbedSpaceId decouples them: NULL (the default this migration gives every existing row) means "use
// Model", so the current index — keyed by the home model name — stays valid with NO reindex and no
// data change. A route that WANTS to share a space with another provider sets EmbedSpaceId to the
// common key; Model still carries the per-provider API string. Embed-only; Chat/Rerank ignore it.
//
// The ADD COLUMN has a typed FluentMigrator form (unlike M039's composite FK), so no raw DDL is needed.
[Migration(46, "Add llm_routes.EmbedSpaceId (nullable canonical vector-index key, decoupled from provider Model)")]
public sealed class M046_LlmRouteEmbedSpaceId : Migration
{
	public override void Up() =>
		Create.Column("EmbedSpaceId").OnTable("llm_routes").AsString().Nullable();

	public override void Down() => Delete.Column("EmbedSpaceId").FromTable("llm_routes");
}
