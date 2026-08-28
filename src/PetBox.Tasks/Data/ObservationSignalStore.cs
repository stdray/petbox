using LinqToDB;
using PetBox.Core.Data;

namespace PetBox.Tasks.Data;

// CRUD over `observation_signal` (M023) — the recurrence counter for kind `observation`
// nodes (work observation-kind-and-dedup). Lives in the project's tasks file, next to
// plan_nodes, via IScopedDbFactory<TasksDb> — same shape as RelationStore/TagStore.
public interface IObservationSignalStore
{
	// First sighting: called once, right after a genuinely NEW observation node is created
	// (no dedup match found). RecurrenceCount starts at 1 — "seen once" — not 0, so the
	// counter always reads as a sighting count, never as "recurrences beyond the first".
	Task RecordFirstSeenAsync(string projectKey, string nodeId, CancellationToken ct = default);

	// A dedup hit: an incoming write matched this node's text instead of becoming a new
	// node. Bumps RecurrenceCount and LastSeenAt; when `currentlyFixed` is true (the matched
	// node's live status is `fixed`), also stamps RecurredAfterFixAt — the regression
	// signal a neighboring detector card reads. Returns the new RecurrenceCount so the
	// caller can report it without a second read. Upserts defensively (INSERT OR REPLACE)
	// even though RecordFirstSeenAsync should always have run first, so a pre-existing
	// observation node from before this table existed still gets a sane row instead of an
	// unhandled miss.
	Task<long> RecordRecurrenceAsync(string projectKey, string nodeId, bool currentlyFixed, CancellationToken ct = default);

	Task<ObservationSignal?> GetAsync(string projectKey, string nodeId, CancellationToken ct = default);
}

public sealed class ObservationSignalStore(IScopedDbFactory<TasksDb> factory) : IObservationSignalStore
{
	public async Task RecordFirstSeenAsync(string projectKey, string nodeId, CancellationToken ct = default)
	{
		using var ctx = factory.NewEnsuredConnection(projectKey);
		await ctx.InsertOrReplaceAsync(new ObservationSignal
		{
			NodeId = nodeId,
			RecurrenceCount = 1,
			LastSeenAt = DateTime.UtcNow,
		}, token: ct);
	}

	public async Task<long> RecordRecurrenceAsync(string projectKey, string nodeId, bool currentlyFixed, CancellationToken ct = default)
	{
		using var ctx = factory.NewEnsuredConnection(projectKey);
		var now = DateTime.UtcNow;
		var existing = await ctx.GetTable<ObservationSignal>().FirstOrDefaultAsync(s => s.NodeId == nodeId, ct);
		var updated = existing is null
			? new ObservationSignal { NodeId = nodeId, RecurrenceCount = 2, LastSeenAt = now, RecurredAfterFixAt = currentlyFixed ? now : null }
			: existing with
			{
				RecurrenceCount = existing.RecurrenceCount + 1,
				LastSeenAt = now,
				RecurredAfterFixAt = currentlyFixed ? now : existing.RecurredAfterFixAt,
			};
		await ctx.InsertOrReplaceAsync(updated, token: ct);
		return updated.RecurrenceCount;
	}

	public async Task<ObservationSignal?> GetAsync(string projectKey, string nodeId, CancellationToken ct = default)
	{
		using var ctx = factory.NewEnsuredConnection(projectKey);
		return await ctx.GetTable<ObservationSignal>().FirstOrDefaultAsync(s => s.NodeId == nodeId, ct);
	}
}
