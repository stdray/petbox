using PetBox.Memory.Data;
using PetBox.Memory.Services;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;

namespace PetBox.Tests.Search;

// The projection invariant search-key-column-everywhere shipped: a node/entry's slug-like Key is
// carried in the SearchDoc's OWN `Key` column, never spliced into `Text` — that splice
// (search-slug-words-gap) double-counted the key's words into Text's BM25 term
// frequencies alongside whatever the title/body already contributed, skewing ranking toward any
// node that merely shares a key word over one that is actually topical. TasksHybridSearchTests'
// SlugWords_DoNotOutrankATopicalMatch was meant to guard the ranking consequence, but its decoy
// is a weak lure (see its comment) and passes identically whether the splice is present or absent
// — it does not close the gap search-key-column-splice-removal-unpinned flagged: the ToDoc SHAPE
// itself was never asserted, so a future "helpfully" reinstated splice ships on a green suite.
//
// This is the direct, cheap version instead: assert the PROJECTION shape (Text excludes the key's
// words, Key carries them) rather than an end-to-end ranking outcome. It needs no DB, no FTS, no
// BM25 weights — it fails the instant either ToDoc goes back to `key + "\n" + name + "\n" + body`,
// independent of how ranking happens to shake out for any particular corpus.
public sealed class SearchDocKeyProjectionTests
{
	[Fact]
	public void Tasks_ToDoc_CarriesKeyWordsOnlyInKeyColumn_NotInText()
	{
		var node = new PlanNode
		{
			Board = "b",
			Key = "kql-spans-query",
			NodeId = "n1",
			Status = "open",
			Type = "feature",
			Name = "Заголовок",
			Body = "Тело узла без латиницы.",
		};

		var doc = TasksSearchDocs.ToDoc(node, "proj", tags: []);

		doc.Key.Should().Be("kql-spans-query");
		doc.Text.Should().NotContain("kql");
		doc.Text.Should().NotContain("spans");
		doc.Text.Should().NotContain("query");
		doc.Text.Should().Be("Заголовок\nТело узла без латиницы.");
	}

	[Fact]
	public void Memory_ToDoc_CarriesKeyWordsOnlyInKeyColumn_NotInText()
	{
		var entry = new MemoryEntry
		{
			Store = "notes",
			Key = "kql-spans-query",
			Type = MemoryType.Project,
			Description = "Заголовок",
			Body = "Тело записи без латиницы.",
		};

		var doc = MemorySearchDocs.ToDoc(entry, "proj");

		doc.Key.Should().Be("kql-spans-query");
		doc.Text.Should().NotContain("kql");
		doc.Text.Should().NotContain("spans");
		doc.Text.Should().NotContain("query");
		doc.Text.Should().Be("Заголовок\nТело записи без латиницы.");
	}
}
