using FluentMigrator;

namespace PetBox.Core.Data.Migrations;

// An endpoint may carry extra request headers (LlmEndpoint.Headers), stored as a JSON object.
// Motivation: the opencode-go gateway answers 400 MissingSessionID unless every request carries
// `x-opencode-session: <uuid>`, and the router previously set only Authorization.
//
// NULLABLE and additive: NULL = no extra headers = today's behaviour, so every existing row is
// unchanged. Down() drops the column; the values live in configuration, not in user data.
[Migration(57, "Add Headers to LlmEndpoints (extra request headers per endpoint)")]
public sealed class M057_LlmEndpointHeaders : Migration
{
	public override void Up() =>
		Create.Column("Headers").OnTable("llm_endpoints").AsString().Nullable();

	public override void Down() => Delete.Column("Headers").FromTable("llm_endpoints");
}
