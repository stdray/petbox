using LinqToDB.Mapping;

namespace PetBox.Core.Models;

// One uploaded body awaiting substitution (work/write-body-by-reference). See
// PetBox.Core.Contract.BodyRefs for the shape decisions this row implements.
//
// STORED IN core.db, not in a mounted volume. A file-backed store would need Dockerfile/compose
// changes to get a writable path onto the host, and the image can build perfectly green while that
// volume never materializes on prod — the failure would appear only as uploads 500ing after a
// deploy. A table ships with the migration that creates it.
//
// The blob is TEXT, not bytes: the reason this mechanism exists is to carry a BODY, and every
// consumer of it (a node body, a memory entry, a comment, a transcript message) is a string. The
// upload endpoint decodes strictly as UTF-8 and refuses anything that is not, rather than storing
// bytes that would later become mojibake nobody can attribute.
[Table("BodyRefBlobs")]
public sealed record BodyRefBlob
{
	// The opaque reference handed back to the uploader (BodyRefs.NewReference).
	[Column, PrimaryKey, NotNull]
	public string Ref { get; init; } = string.Empty;

	// The project the blob was uploaded INTO — the tenant, and the only thing that decides who may
	// later reference it. Not the uploading ApiKey: "one agent uploads, another references" is the
	// fan-out case the mechanism exists for, and keying on the key would break it.
	[Column, NotNull]
	public string ProjectKey { get; init; } = string.Empty;

	[Column, NotNull]
	public string Body { get; init; } = string.Empty;

	// Bytes AS UPLOADED (before UTF-8 decoding). Kept because it, not Body.Length, is what the
	// ceiling was enforced on — an operator reading this row must see the number that was judged.
	[Column]
	public long Bytes { get; init; }

	[Column]
	public DateTime CreatedAt { get; init; }

	// CreatedAt + BodyRefs.Ttl. Stored rather than computed so the prune job's WHERE clause is a
	// plain indexed comparison and the TTL of an already-uploaded blob cannot change under it when
	// the constant is edited.
	[Column]
	public DateTime ExpiresAt { get; init; }

	// The name of the ApiKey that uploaded it (spec access-attribution). Never read by the
	// substitution path — it is there so an operator looking at an unconsumed blob can tell who
	// left it.
	[Column, NotNull]
	public string CreatedBy { get; init; } = string.Empty;
}
