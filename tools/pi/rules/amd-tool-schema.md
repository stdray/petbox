## Tool schemas before calling — [own]

1. A tool declared in the system block carries its schema there — read it, do not guess.
2. An MCP tool not in the declarations (codemode `tools.mcp__*`) — one `describeTool`
   before the first call, once per tool per session, not before every call.
3. An argument not in the schema is not passed. No "by meaning" additions.

A reply shaped `must have required properties 'X'` is not "it failed" — it is "the
schema was not read". A second identical refusal on the same tool = stop and read the
schema, not a third guess with a different shape.
