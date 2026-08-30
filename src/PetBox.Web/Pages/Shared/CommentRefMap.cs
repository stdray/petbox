using PetBox.Tasks.Contract;
using PetBox.Web.Rendering;

namespace PetBox.Web.Pages.Shared;

// Builds the `[[#comment]]` resolution map handed to the markdown renderer (work
// `comment-slug-and-refs`, spec `comment-ref-links`) — the comment sibling of NodeRefMap and
// MemoryRefMap.
//
// THE ONE THING THIS FILE IS FOR: it takes the comments the caller is ACTUALLY RENDERING and
// nothing else. That single argument is the entire confinement mechanism (spec
// `node-share-confinement`), and it is why the renderer needs no "public" mode and no branch on who
// is reading — the four surfaces differ only in what they pass:
//
//   TaskBoardNode (private)   every comment of the node        → every reference links
//   ShareNode scope=full      the whole published thread       → references work inside the share
//   ShareNode scope=body      nothing (an EMPTY map)           → every reference stays plain text
//   ShareNode scope=comment   the ONE published comment        → a self-reference links, a
//                                                                reference to a neighbour stays text
//
// The last row is a security property, not a convenience: a link to a comment outside the grant
// would either lead into a UI the reader cannot open or, worse, disclose that the neighbouring
// comment exists at all. Degrading to text withholds both, and it is the family's ordinary
// miss-behaviour rather than a special case someone has to remember to write.
//
// Unlike its two siblings this map needs NO service and NO query: a comment reference is an in-page
// anchor (`#comment-{id}`) to something the page is already rendering, so the rows it resolves
// against are the rows the caller already holds. That is also why v1 is limited to comments of the
// SAME node — a cross-node reference would need a resolution map with no natural bound, and the
// confinement question would have to be answered again for it.
public static class CommentRefMap
{
	// Two keys per rendered comment — its 32-hex id, and its slug when it has one — both pointing at
	// the same in-page anchor. Both are addresses of the same comment, so both must resolve: the id
	// is what the "copy reference" affordance hands an author (nothing has a slug until someone
	// chooses one), the slug is what a human writes and reads.
	public static IReadOnlyDictionary<string, NodeRefTarget> Build(IEnumerable<CommentView> rendered)
	{
		var map = new Dictionary<string, NodeRefTarget>(StringComparer.Ordinal);
		foreach (var c in rendered)
		{
			// The anchor `_CommentThread.cshtml` already emits on every rendered comment
			// (comment-permalink-anchor). Relative, no scheme — so it survives the sanitizer, and it
			// stays correct on both the private page and the public share page, which render the
			// same partial and therefore the same `id="comment-{id}"`.
			var url = "#comment-" + c.Id;
			// Anchor text and tooltip: author + date, NOT the first words of the body. The body is
			// the part that gets edited; the attribution is not, so a reference written today still
			// reads correctly after the target is rewritten (the idea's own reasoning). It also
			// discloses nothing new — the map only ever holds comments the reader is already
			// looking at, whose author and date are on screen a few lines away.
			var when = c.Created.ToString("yyyy-MM-dd");
			var target = new NodeRefTarget(url, $"comment by {c.Author} · {when}", $"{c.Author} · {when}");
			map[c.Id] = target;
			if (!string.IsNullOrEmpty(c.Slug))
				// The slug is what the author WROTE, so it stays the visible text — the same rule
				// `[[slug]]` node mentions follow. Only the id form needs a substitute.
				map[c.Slug] = target with { Text = null };
		}
		return map;
	}
}
