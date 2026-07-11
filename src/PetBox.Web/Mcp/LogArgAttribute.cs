namespace PetBox.Web.Mcp;

// Opt-in markup for the ONE privacy-safe knob a tool parameter may contribute to telemetry
// (span tag + the ToolCalls self-log event). Variant B of `toolcalls-log-params`: the decision
// lives NEXT TO the parameter, not in a hardcoded allowlist far away in the tracing filter —
// one source of truth, and a new tool's knob is auditable in its own signature.
//
// PRIVACY CONTRACT (spec: trace-mcp-call-shape). Marking a parameter is an EXPLICIT ASSERTION
// that its value carries no user content and no secrets — it is a knob (a number, a bool, a
// closed enum-like token whose alphabet OUR contract fixes, not the caller's typing). The SAFE
// DEFAULT is to leave a parameter UNMARKED: an unmarked parameter is never named and never
// valued in telemetry.
// Anything free-text (`q`, titles, bodies, keys people chose) must be [LogArg(LogArgMode.Presence)]
// at most — never Value.
enum LogArgMode
{
	// Log the VALUE. Only meaningful for numeric/bool/enum-like knobs (bodyLen, limit, includeUsage).
	Value,

	// Log ONLY a bool: was the arg passed and non-empty. The value itself NEVER leaves the process.
	// This is the mode for free-text params whose PRESENCE is the interesting shape (`q`: did this
	// call search, or merely list?).
	Presence,
}

[AttributeUsage(AttributeTargets.Parameter)]
sealed class LogArgAttribute : Attribute
{
	public LogArgAttribute(LogArgMode mode = LogArgMode.Value) => Mode = mode;

	public LogArgMode Mode { get; }
}
