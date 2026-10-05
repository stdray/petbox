using FluentMigrator;

namespace PetBox.Core.Data.Migrations;

// A chat route gets to say how hard the model may think, in OpenRouter's dialect.
//
// The gap this closes is measured, not hypothetical. `llm_routes.Thinking` (M039) carries the
// DEEPSEEK dialect — `thinking: {type: enabled|disabled}` — and every chat route has used it. On an
// OpenRouter endpoint it buys nothing: `reasoning` is a different field, and a reasoning model
// with `reasoning.default_enabled: true` reasons by default. A trial on
// inclusionai/ling-3.0-flash-sante:free at `max_tokens: 64` returned empty content 4/6 times with
// `thinking: disabled` set and 5/8 times without it — the flag was noise.
//
// Why that emptiness is worth a column: reasoning tokens are billed against `max_tokens`, so a
// small budget can be spent entirely on reasoning, leaving `content: ""` with
// `finish_reason: "length"`. `LlmRoute.Reasoning` renders `reasoning: {effort: …}`, and `none`
// turns reasoning off, which is the only configuration under which a small budget still yields an
// answer. Evidence: observations board, llm-chat-free-reasoning-model-empty-text-small-max-tokens.
//
// NULLABLE and additive on purpose. NULL = "send no reasoning field" = today's behaviour exactly,
// so every existing row is unchanged and no route has to be edited to keep working; the column is
// read by the resolver and written by the admin surface, never by a background job. Short (16) —
// the widest stored value is "Minimal"; TEXT would do, and SQLite does not enforce the length
// anyway, but the other enum-ish column in this table (Thinking) is bounded the same way.
//
// Down() drops the column: the values live in configuration, not in user data, and a level that
// still holds them simply resolves them to nothing.
[Migration(56, "Add Reasoning to LlmRoutes (OpenRouter-dialect reasoning effort for chat routes)")]
public sealed class M056_LlmRouteReasoning : Migration
{
	public override void Up() =>
		Create.Column("Reasoning").OnTable("llm_routes").AsString(16).Nullable();

	public override void Down() => Delete.Column("Reasoning").FromTable("llm_routes");
}
