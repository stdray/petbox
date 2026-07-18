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
// BM25 weights — it fails the instant either ToDoc goes back to splicing key/title into `Text`,
// independent of how ranking happens to shake out for any particular corpus.
//
// search-doc-model-title-weights extended the same discipline to the TITLE: the title is its own
// declared field (Title column), `Text` is the BODY alone, and the embed-template (EmbedInput)
// recombines Title+Body — so these assertions also pin that the title is neither missing nor
// re-spliced into the body, and that the semantic input is unchanged from the old spliced Text.
public sealed class SearchDocKeyProjectionTests
{
	[Fact]
	public void Tasks_ToDoc_SeparatesKey_Title_And_Body()
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

		// Key words: only in Key, never spliced into Text.
		doc.Key.Should().Be("kql-spans-query");
		doc.Text.Should().NotContain("kql").And.NotContain("spans").And.NotContain("query");
		// Title is its own field; Text is the body ALONE (no title splice).
		doc.Title.Should().Be("Заголовок");
		doc.Text.Should().Be("Тело узла без латиницы.");
		// The declared embed-template recombines Title+Body — byte-identical to the old spliced Text,
		// so existing semantic vectors stay valid.
		doc.EmbedInput.Should().Be("Заголовок\nТело узла без латиницы.");
	}

	[Fact]
	public void Memory_ToDoc_SeparatesKey_Title_And_Body()
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
		doc.Text.Should().NotContain("kql").And.NotContain("spans").And.NotContain("query");
		// A memory entry's Description IS its title — a free port to the Title field.
		doc.Title.Should().Be("Заголовок");
		doc.Text.Should().Be("Тело записи без латиницы.");
		doc.EmbedInput.Should().Be("Заголовок\nТело записи без латиницы.");
	}
}
