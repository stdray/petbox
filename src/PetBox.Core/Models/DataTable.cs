using LinqToDB.Mapping;

namespace PetBox.Core.Models;

[Table("DataTables")]
public sealed record DataTable
{
	[PrimaryKey]
	public string Name { get; init; } = string.Empty;

	public string ProjectKey { get; init; } = string.Empty;
	public string Columns { get; init; } = "[]"; // JSON array of { name, type, pk, notNull }
	public bool Read { get; init; } = true;
	public bool Write { get; init; }
	public bool Delete { get; init; }

	// NOT a column: M005 never created one for it, and no code reads it. Declaring it in PetBoxDb's
	// mapping would break INSERT ("no such column"), so it is opted out explicitly — the alternative
	// (leaving it undeclared and unmarked) is indistinguishable from the Fluent-mapping trap
	// FluentMappingCompletenessTests exists to catch.
	[NotColumn]
	public bool Created { get; init; }
}
