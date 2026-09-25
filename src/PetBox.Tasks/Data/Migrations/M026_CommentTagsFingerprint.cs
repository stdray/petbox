using System.Data;
using FluentMigrator;
using PetBox.Core.Data;

namespace PetBox.Tasks.Data.Migrations;

// comments.TagsFingerprint — card comment-tags-only-patch-keeps-version (observation
// comment-tags-only-patch-keeps-version, promoted). A comment's tags live in comment_tag, an
// SCD-2 association keyed by CommentId — NOT a column of the temporal `comments` row — so
// CommentRow.SamePayload (Body/Author/ParentId/Slug only) never saw a tags-only PATCH as a
// change: TemporalStore classified it as an identical-payload no-op, leaving Version/Updated
// unmoved even though `applied:true` came back and the tags DID land (CommentService writes
// comment_tag unconditionally whenever the batch applies). That made a tags-only edit invisible
// to comments_delta's version cursor and to a concurrent writer's stale-baseline CAS check — a
// second writer with an old baseline could clobber a tag change silently.
//
// The fix threads a deterministic summary of the ACTIVE tag set through the row itself, as a
// real payload field compared by SamePayload/ChangedPayloadFields (CommentService.TagFingerprint:
// normalize -> sort ordinal -> join U+001F, mirroring SetTagsAsync's own normalization so the two
// never disagree). Once it is part of the payload, a tags-only PATCH mints an ordinary new
// revision like a body or slug edit — new Version, new Updated, visible to comments_delta, and a
// stale baseline against it conflicts like any other field.
//
// BACKFILL, not just an added column: every comment written before this field existed has
// TagsFingerprint = "" by construction (ALTER TABLE ... DEFAULT ''), which would silently
// disagree with a comment that already carries tags — the very first tags-only patch on such a
// comment would misclassify as a semantic change (an extra, harmless version bump) rather than
// recognizing an identical resubmit as the no-op it is. So every ACTIVE comment's fingerprint is
// computed here from its current comment_tag rows, using the exact same algorithm CommentService
// applies going forward (tags are already normalized on write, so no re-normalization is needed
// here — only sort + join). Read fully, then write (M055's precedent): issuing UPDATEs against an
// open reader is provider-dependent, so the rewrite is buffered.
[Migration(26, "comments.TagsFingerprint — payload field so a tags-only PATCH mints a real revision")]
public sealed class M026_CommentTagsFingerprint : SqliteMigration
{
	public override void Up()
	{
		Alter.Table("comments").AddColumn("TagsFingerprint").AsString().NotNullable().WithDefaultValue("");

		Execute.WithConnection((conn, tx) =>
		{
			var tagsByComment = new Dictionary<string, List<string>>(StringComparer.Ordinal);
			using (var read = conn.CreateCommand())
			{
				read.Transaction = tx;
				read.CommandText = "SELECT \"CommentId\", \"Tag\" FROM \"comment_tag\" WHERE \"ValidTo\" IS NULL;";
				using var reader = read.ExecuteReader();
				while (reader.Read())
				{
					var commentId = reader.GetString(0);
					var tag = reader.GetString(1);
					if (!tagsByComment.TryGetValue(commentId, out var list))
						tagsByComment[commentId] = list = [];
					list.Add(tag);
				}
			}

			// Nothing to backfill on a fresh install (no comment_tag rows yet) or on a project
			// where every comment is untagged — the expected shape for most callers of this
			// migration, not a failure.
			foreach (var (commentId, tags) in tagsByComment)
			{
				tags.Sort(StringComparer.Ordinal);
				var fingerprint = string.Join('\u001f', tags);

				using var write = conn.CreateCommand();
				write.Transaction = tx;
				write.CommandText =
					"UPDATE \"comments\" SET \"TagsFingerprint\" = @fp WHERE \"Key\" = @key AND \"ActiveTo\" IS NULL;";
				Bind(write, "@fp", fingerprint);
				Bind(write, "@key", commentId);
				write.ExecuteNonQuery();
			}
		});
	}

	public override void Down() { } // forward-only

	static void Bind(IDbCommand cmd, string name, string value)
	{
		var p = cmd.CreateParameter();
		p.ParameterName = name;
		p.Value = value;
		cmd.Parameters.Add(p);
	}
}
