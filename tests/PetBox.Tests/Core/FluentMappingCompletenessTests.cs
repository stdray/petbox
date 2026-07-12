using System.Reflection;
using LinqToDB;
using LinqToDB.Mapping;
using PetBox.Core.Data;

namespace PetBox.Tests.Mapping;

// THE linq2db Fluent-mapping trap, made impossible to repeat.
//
// PetBoxDb maps its entities through a FluentMappingBuilder into one shared MappingSchema. On an
// entity the builder touches, linq2db takes the DECLARED properties as the whole truth: a property
// that exists on the model — and as a real migration column — but is NOT declared in PetBoxDb is
// silently dropped from the schema cache. The failure is the quiet kind:
//   * InsertAsync omits the column (a NOT NULL one then 500s; a nullable one just… vanishes);
//   * reads return the CLR default (null) for it;
//   * and the call REPORTS SUCCESS.
// Scars: SavedQuery.CreatedAt/UpdatedAt ("Save as" 500'd), ApiKey.ExpiresAt, and — on this branch —
// ApiKey.DefaultProjectKey, which round-tripped as NULL while apikey_create happily returned it.
//
// A migration test does NOT catch this (the column IS in the DB), and neither does anything that
// only writes through raw SQL. So the guard is here, on the mapping itself: for every entity PetBoxDb
// exposes, every public settable property of the model must be a COLUMN in the schema linq2db actually
// uses. Add a property to a mapped entity and forget PetBoxDb → this test goes red, naming it.
//
// The trap is not uniform across entities, and the test deliberately does not try to predict where it
// bites — it asks the ONE question that is always the right one ("is this property a column in the
// schema linq2db will use?") of EVERY entity, however it is mapped:
//   * attribute-carrying + Fluent (ApiKey, DataTable, SavedQuery, ShareLink — the [Table] ones): the
//     Fluent declaration becomes the whole truth and an undeclared property is DROPPED. This is the
//     trap, and this is where the test goes red (verified by adding a scratch property to ApiKey).
//   * pure-attribute (LogMeta, DataDb, HealthReport, …): the [Column] attributes declare everything;
//     an undeclared property would be dropped the same way, and would be caught here the same way.
//   * pure-Fluent, no attributes at all (Workspace, Project, User, …): linq2db keeps auto-mapping the
//     undeclared properties, so nothing is dropped and nothing fires — correctly. The moment such an
//     entity grows an attribute, it joins the first bucket and this test starts guarding it.
public sealed class FluentMappingCompletenessTests
{
	// The mapping linq2db really uses at runtime: the shared schema PetBoxDb hands to every connection
	// (CreateOptions → UseMappingSchema). Not re-derived here — that is the whole point.
	static MappingSchema Schema()
	{
		using var db = new PetBoxDb(PetBoxDb.CreateOptions("Data Source=:memory:"));
		return db.MappingSchema;
	}

	// Every entity core.db routes through PetBoxDb — its ITable<T> properties ARE the entity set, so a
	// new table is covered the day its ITable<> is added.
	public static TheoryData<Type> Entities()
	{
		var data = new TheoryData<Type>();
		foreach (var entity in EntityTypes()) data.Add(entity);
		return data;
	}

	static IEnumerable<Type> EntityTypes() =>
		typeof(PetBoxDb)
			.GetProperties(BindingFlags.Public | BindingFlags.Instance)
			.Select(p => p.PropertyType)
			.Where(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(ITable<>))
			.Select(t => t.GetGenericArguments()[0])
			.Distinct();

	// Explicit opt-outs: `"<Entity>.<Property>"` → why it is NOT a column. Empty today — every property
	// of every mapped entity is persisted. A genuinely non-persisted property goes here WITH a reason
	// (or gets [NotColumn] on the model, which this test honours), never silently.
	static readonly IReadOnlyDictionary<string, string> NotPersisted =
		new Dictionary<string, string>(StringComparer.Ordinal);

	[Theory]
	[MemberData(nameof(Entities))]
	public void EveryModelProperty_IsDeclaredInTheMapping(Type entity)
	{
		var columns = Schema().GetEntityDescriptor(entity).Columns
			.Select(c => c.MemberName)
			.ToHashSet(StringComparer.Ordinal);

		var missing = entity
			.GetProperties(BindingFlags.Public | BindingFlags.Instance)
			.Where(p => p.CanRead && p.SetMethod is { IsPublic: true })          // init-only counts: CanWrite
			.Where(p => p.GetCustomAttribute<NotColumnAttribute>() is null)      // explicitly not a column
			.Where(p => !NotPersisted.ContainsKey($"{entity.Name}.{p.Name}"))
			.Select(p => p.Name)
			.Where(name => !columns.Contains(name))
			.OrderBy(name => name, StringComparer.Ordinal)
			.ToList();

		missing.Should().BeEmpty(
			$"every persisted property of {entity.Name} must be declared in PetBoxDb's mapping — linq2db "
			+ "drops an undeclared property of a Fluent-mapped entity from its schema cache, so INSERT omits "
			+ "the column, the read comes back NULL, and the call still reports SUCCESS. Declare it in "
			+ "PetBoxDb.BuildMappingSchema (`.Property(x => x.Foo)…`), then prove it with an INSERT→SELECT "
			+ "round-trip test — a migration-only test does NOT catch this. If the property is genuinely not "
			+ "persisted, mark it [NotColumn] or add it to NotPersisted with a reason. Undeclared: "
			+ string.Join(", ", missing));
	}

	// The guard above is only as good as its entity set: if PetBoxDb's ITable<> properties stopped being
	// discoverable, it would pass vacuously. Pin the count's floor and the entities carrying the known
	// scars, so an empty theory can never be mistaken for a green one.
	[Fact]
	public void TheEntitySet_IsDiscovered()
	{
		var entities = EntityTypes().ToList();

		entities.Should().HaveCountGreaterThan(10);
		entities.Select(t => t.Name).Should().Contain(["ApiKey", "SavedQuery", "ShareLink"]);
	}
}
