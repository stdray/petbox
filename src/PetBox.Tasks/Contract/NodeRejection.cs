namespace PetBox.Tasks.Contract;

// A guard refusal that indicts ONE node of a batch carries that node's key — WITHOUT changing the
// exception's type. The type is load-bearing: callers (and tests) read `ArgumentException` as "your
// payload is malformed" and `InvalidOperationException` as "the process refused", and xUnit's
// Assert.Throws<T> matches EXACTLY, so a subclass would silently break the atomic contract's own
// tests. The key rides Exception.Data instead — invisible to every existing path, and exactly what
// the PARTIAL path needs to turn the same refusal into a per-entry Rejected conflict rather than
// failing the whole call.
//
// A batch-SHAPE error (a node both deleted and upserted, a rename that is also a delete, a closed
// board) is deliberately NOT tagged: it indicts the CALL, not an entry, and must keep failing the
// call even in partial mode.
public static class NodeRejection
{
	const string DataKey = "petbox.node_key";

	// Tag a refusal with the node it indicts. Returns the exception so it can be thrown inline.
	public static T ForNode<T>(this T ex, string key) where T : Exception
	{
		ex.Data[DataKey] = key;
		return ex;
	}

	// The node this refusal indicts, or null when it indicts the whole call.
	public static string? RejectedNode(this Exception ex) => ex.Data[DataKey] as string;
}
