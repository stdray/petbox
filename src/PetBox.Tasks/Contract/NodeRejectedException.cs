namespace PetBox.Tasks.Contract;

// A guard refusal that indicts ONE node of the batch, and says which one. The exception TYPES
// are unchanged (ArgumentException for a bad payload/ref, InvalidOperationException for a
// workflow-gate refusal) — same messages, same HTTP shape — so the ATOMIC path behaves bit for
// bit as before. The added `Key` is what lets the PARTIAL path turn the very same refusal into
// a per-entry Rejected conflict instead of failing the whole call.
//
// A batch-SHAPE error (a node both deleted and upserted, a rename that is also a delete, a
// closed board) deliberately does NOT implement this: it indicts the CALL, not an entry, and
// must keep failing the call even in partial mode.
public interface INodeRejection
{
	string Key { get; }
	string Message { get; }
}

public sealed class NodeRejectedException(string key, string message)
	: ArgumentException(message), INodeRejection
{
	public string Key { get; } = key;
}

// The workflow/precondition gates historically throw InvalidOperationException — keep that
// type (callers and tests read it as "the process refused", not "your input is malformed").
public sealed class NodeGateException(string key, string message)
	: InvalidOperationException(message), INodeRejection
{
	public string Key { get; } = key;
}
