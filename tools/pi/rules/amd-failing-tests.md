## After a failing test or build — [opencode]

When a build/test invocation returns non-zero, the NEXT call is one of: grep the error
string in non-test source (who produces it, which branch); read the non-test source the
test exercises; webfetch `"<error excerpt>" <framework>` if web tools are present.
Editing the test file at this step is forbidden. Test instrumentation (logging, dumps,
probes, timing guards) is allowed only after at least one of those reads.
