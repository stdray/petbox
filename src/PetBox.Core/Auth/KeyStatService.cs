using System.Collections.Concurrent;

namespace PetBox.Core.Auth;

// spec apikey-last-used. THE in-memory record of "when was this key last used" — a SINGLETON, and
// the only thing the auth hot path touches for it: two ConcurrentDictionary writes, zero DB work.
// KeyStatFlusher folds the marks into ApiKeys.LastUsedAt in ONE batched statement about every five
// minutes (same shape as MemoryUsageRecorder: a singleton accumulates, a background loop drains).
//
// Consequences, stated honestly:
//  - the STORED value is coarse — it trails reality by up to one flush window;
//  - a hard restart loses up to one window's worth of marks (a graceful shutdown flushes);
//  - therefore every read that must be fresh (apikey_list, admin UI) merges DB with LastUsed() and
//    takes the LATER of the two — otherwise the UI would lag five minutes behind a call that has
//    already happened.
// For the question the column exists to answer — "is this key still in use?" — that is enough.
public interface IKeyStatService
{
	// Called on the auth path for every successful authentication. Must stay allocation-light.
	void Stamp(string key);

	// The freshest mark this process has seen, or null if it has seen none. Not the stored value —
	// callers merge it with the DB row (see ApiKeyTools.ListAsync).
	DateTime? LastUsed(string key);

	// Everything stamped since the previous drain, as one batch for one statement. Consumes the
	// dirty MARKS only: LastUsed() keeps answering from memory afterwards.
	IReadOnlyList<KeyValuePair<string, DateTime>> DrainDirty();
}

public sealed class KeyStatService : IKeyStatService
{
	readonly ConcurrentDictionary<string, DateTime> _lastUsed = new(StringComparer.Ordinal);
	readonly ConcurrentDictionary<string, byte> _dirty = new(StringComparer.Ordinal);

	public void Stamp(string key)
	{
		if (string.IsNullOrEmpty(key)) return;
		var now = DateTime.UtcNow;
		// Monotonic: concurrent callers on one key may interleave, and the LATEST stamp must win —
		// never an older one that happened to commit second.
		_lastUsed.AddOrUpdate(key, now, (_, previous) => now > previous ? now : previous);
		// Marked dirty AFTER the value is visible, which is what makes the drain below lossless.
		_dirty[key] = 0;
	}

	public DateTime? LastUsed(string key) =>
		_lastUsed.TryGetValue(key, out var at) ? at : null;

	public IReadOnlyList<KeyValuePair<string, DateTime>> DrainDirty()
	{
		var batch = new List<KeyValuePair<string, DateTime>>();
		foreach (var key in _dirty.Keys)
		{
			// Clear the mark FIRST, then read the value: a Stamp racing us either lands before the
			// read (we flush the newer value, and its re-marking just re-flushes the same value next
			// pass) or after it (the key is dirty again and the next pass carries it). Neither order
			// drops a stamp — the failure mode is one redundant write, never a lost one.
			if (!_dirty.TryRemove(key, out _)) continue;
			if (_lastUsed.TryGetValue(key, out var at))
				batch.Add(new KeyValuePair<string, DateTime>(key, at));
		}
		return batch;
	}
}
