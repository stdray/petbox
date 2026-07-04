using LinqToDB.Mapping;

namespace PetBox.Core.Data.Temporal;

// Base row of a bitemporal (SCD type-2) store: one logical record is identified
// by Key, its revisions are numbered by Version, and the *active* revision is the
// one whose ActiveTo is null. Payload columns are defined by the derived type;
// the engine (TemporalStore) only knows the identity/temporal columns plus the
// two hooks below.
public abstract record TemporalRow
{
	[Column, NotNull] public string Key { get; init; } = string.Empty;

	// Revision number of this row. On a submitted (desired) row it carries the
	// version the author last saw; 0 means "I believe this is a new node".
	[Column] public long Version { get; init; }

	[Column] public long ActiveFrom { get; init; }

	// null => this is the current (active) revision.
	[Column, Nullable] public long? ActiveTo { get; init; }

	// Lineage edge: on a rename, the desired row sets PrevKey to the old Key. The
	// engine retires the active row at PrevKey and creates this one at Key, so the
	// birth revision of the new identity records where it came from. NOT payload —
	// excluded from SamePayload; reconstruct rename history by walking PrevKey.
	[Column, Nullable] public string? PrevKey { get; init; }

	[Column] public DateTime Created { get; init; }
	[Column] public DateTime Updated { get; init; }

	// Compares ONLY the payload (ignores Key/Version/temporal columns). Lets the
	// engine collapse no-op resubmits and absorb identical concurrent edits.
	public abstract bool SamePayload(TemporalRow other);

	// Names of payload fields whose values differ from `other` — the informed-conflict
	// surface: a Stale answer names WHAT moved past the author's baseline so the caller
	// rebases on facts instead of a blind re-read. Must agree with SamePayload (empty
	// exactly when SamePayload is true). Default: empty — a row type that does not
	// override still conflicts correctly, just without the field list.
	public virtual IReadOnlyList<string> ChangedPayloadFields(TemporalRow other) => [];

	// Returns a copy of this row as a fresh active revision numbered `version`.
	// Derived records implement via `this with { ... }`.
	public abstract TemporalRow AsRevision(long version, DateTime created, DateTime updated);
}
