namespace PetBox.Web.Mcp;

// Declares that a BATCH-ITEM property is genuinely REQUIRED, so the generated input schema says so
// instead of only the prose saying so.
//
// Why an attribute and not the C# `required` modifier. These item records (PlanNodeInput and
// friends) are deserialized by the SDK BEFORE our tool body runs. Marking the property `required`
// in C# makes STJ throw its own JsonException at bind time ("missing required properties"), which
// reaches the caller as a raw -32602 instead of the tool's own sentence ("each node needs a 'key'
// (a flat slug)"). We want BOTH the honest schema and the good message, so the CLR property stays
// nullable and this attribute carries the contract into the published schema.
//
// Why it is stamped post-hoc (McpOutputSchema.WithRequiredMembers) rather than in
// TransformSchemaNode: AIJsonSchemaTransformContext exposes only Path/PropertyName — it never hands
// the transform the CLR type or the member's attributes, so the attribute is invisible from inside
// the walk. The MethodInfo we already hold at tool-creation time is the only place that sees it.
// The same reason WithDeclaredShapes exists; this rides the same seam.
//
// HISTORY (drop-legacy-aliases): `key` on tasks_upsert could not carry `required` while the legacy
// `l1` alias was live — no single property can be required when either of two spellings satisfies
// the contract. Retiring `l1` is what makes this attribute expressible at all.
[AttributeUsage(AttributeTargets.Property)]
sealed class McpRequiredMemberAttribute : Attribute;
