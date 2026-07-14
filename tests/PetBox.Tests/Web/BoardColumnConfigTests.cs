using PetBox.Web.Rendering;

namespace PetBox.Tests.Web;

// kanban-column-picker: the column-visibility config every kanban render reads — covered here as
// pure logic (no HTTP round-trip), the same posture BoardFieldConfigTests gives its fixed-
// vocabulary twin. The vocabulary here is always supplied by the CALLER (a board's own
// KanbanColumns-derived slug list), never a static table, since a different methodology has
// different statuses.
public sealed class BoardColumnConfigTests
{
	static readonly IReadOnlyList<string> Known = ["todo", "inProgress", "done"];

	[Fact]
	public void FromKeys_UnknownSlugsAreSilentlyDropped()
	{
		// Never a 500 on a stale saved value once a methodology change removes/renames a status —
		// same tolerance BoardFieldConfig.FromKeys gives an unknown field key.
		var cfg = BoardColumnConfig.FromKeys(["todo", "bogus", "done"], Known);
		cfg.Has("todo").Should().BeTrue();
		cfg.Has("done").Should().BeTrue();
		cfg.Has("bogus").Should().BeFalse();
		cfg.Has("inProgress").Should().BeFalse();
	}

	[Fact]
	public void FromKeys_NullOrEmpty_YieldsNoneVisible()
	{
		BoardColumnConfig.FromKeys(null, Known).Visible.Should().BeEmpty();
		BoardColumnConfig.FromKeys([], Known).Visible.Should().BeEmpty();
	}

	[Fact]
	public void FromKeys_IsCaseInsensitive() =>
		BoardColumnConfig.FromKeys(["TODO"], Known).Has("todo").Should().BeTrue();

	[Fact]
	public void ToCsv_RoundTripsThroughFromKeys()
	{
		var cfg = BoardColumnConfig.FromKeys(["done", "todo"], Known);
		var csv = cfg.ToCsv(Known);
		var roundTripped = BoardColumnConfig.FromKeys(csv.Split(',', StringSplitOptions.RemoveEmptyEntries), Known);
		roundTripped.Visible.Should().BeEquivalentTo(cfg.Visible);
	}

	[Fact]
	public void ToCsv_OrdersBy_KnownSlugsOrder_NotSelectionOrder()
	{
		// Selected in reverse (done, then todo) — ToCsv must still emit Known's own order
		// (todo,done), the same "canonical order the wire value round-trips through" discipline
		// BoardFieldConfig.ToCsv() documents.
		var cfg = BoardColumnConfig.FromKeys(["done", "todo"], Known);
		cfg.ToCsv(Known).Should().Be("todo,done");
	}

	[Fact]
	public void ToCsv_EmptyConfig_IsEmptyString() =>
		BoardColumnConfig.None.ToCsv(Known).Should().BeEmpty();

	[Fact]
	public void AllVisible_HasEveryKnownSlug()
	{
		var cfg = BoardColumnConfig.AllVisible(Known);
		foreach (var slug in Known) cfg.Has(slug).Should().BeTrue();
		cfg.ToCsv(Known).Should().Be("todo,inProgress,done");
	}

	[Fact]
	public void Has_ReadsTheSelectedSet()
	{
		var cfg = new BoardColumnConfig(new HashSet<string>(["todo"], StringComparer.OrdinalIgnoreCase));
		cfg.Has("todo").Should().BeTrue();
		cfg.Has("done").Should().BeFalse();
		cfg.Has("bogus").Should().BeFalse();
	}
}
