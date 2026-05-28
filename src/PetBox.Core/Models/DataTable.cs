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
	public bool Created { get; init; }
}
