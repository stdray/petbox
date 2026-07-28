// Gate labelling on the workflow graph's edges (approval-gate-enforced-visible). `requiresApproval`
// and `enforceApproval` are two DIFFERENT statements — the edge is owner-only vs the server actually
// blocks it — and the graph used to collapse both into a single "approve", which read as a hard gate
// on the presets' soft ones (classic's Review -> Done). These cover the label/tooltip split; the SVG
// geometry around them is exercised by the E2E modal test (tests/PetBox.E2ETests/WorkflowVizTests.cs).
//
// Run: node --test ts/workflow-viz.test.ts   (Node >= 23.6 native TS type-stripping; no build step)
//      or: bun test ts/workflow-viz.test.ts

import assert from "node:assert/strict";
import { test } from "node:test";
import { gateLabel, gateTooltip } from "./workflow-viz.ts";

test("gateLabel: an ungated edge has no label at all", () => {
	assert.equal(gateLabel({ from: "a", to: "b" }), "");
	assert.equal(gateTooltip({ from: "a", to: "b" }), "");
});

test("gateLabel: a SERVER-ENFORCED approval gate says so", () => {
	const edge = { from: "review", to: "done", requiresApproval: true, enforceApproval: true };
	assert.equal(gateLabel(edge), "approve (enforced)");
	assert.match(gateTooltip(edge), /blocks this move/);
});

test("gateLabel: a declared-but-unenforced approval gate is NOT labelled like an enforced one", () => {
	const soft = { from: "review", to: "done", requiresApproval: true };
	const hard = { from: "review", to: "done", requiresApproval: true, enforceApproval: true };
	assert.equal(gateLabel(soft), "approve (not enforced)");
	assert.notEqual(gateLabel(soft), gateLabel(hard));
	// The consequence the compact label can only hint at lives in the hover text.
	assert.match(gateTooltip(soft), /an agent can push this move too/);
});

test("gateLabel: approval marker composes with the other gates, in declaration order", () => {
	assert.equal(
		gateLabel({
			from: "a",
			to: "b",
			requiresApproval: true,
			requiresReason: true,
			preconditionArtifact: "spec_plan",
			checklist: true,
		}),
		"approve (not enforced) reason artifact:spec_plan checklist",
	);
});
