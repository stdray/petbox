using System.Reflection;
using System.Text.Json;
using PetBox.Tasks.Workflow;
using PetBox.Web.Mcp;
using PetBox.Web.Mcp.Contract;

namespace PetBox.Tests.Tasks;

// work/mcp-rules-upsert-is-lossy — CLOSE THE CLASS, not just the 4 fields.
//
// tasks_methodology_rules_upsert / tasks_methodology_template_upsert both funnel through ONE
// wire type, MethodologyDefInput (MethodologyWire.ParseDefinition), and both perform a FULL
// DOCUMENT REPLACE: whatever the caller's MethodologyKindInput does NOT carry is gone from the
// stored MethodologyKindDef after the write — silently, no error, no diagnostic. That is not a
// bug in any one edit; it is a structural property of "input type is a subset of the domain
// type, write is a full replace". It bit prod TWICE under two different names before the
// shared cause was found (AutoWireSpecFrom/Delivery from the quartet verdict-gate rewrite;
// DefaultView from board-view-defaults-not-applied-existing-instances, which patched the
// SYMPTOM with a resolve-time merge and left the wire itself still lossy).
//
// This test asks the ONE question that generalizes: for every {Def, Input} pair in the
// methodology-kind wire, does the input side carry every settable field the domain side does?
// It does NOT rely on a naming convention (Def↔Input suffix swap fails for WorkflowStatus↔
// MethodologyStatusInput and MethodologyTransitionEffectDef↔MethodologyEffectInput) — the pairs
// are named explicitly, so adding a wholly NEW nested Def/Input pair still requires a human to
// wire it into the table below (documented, not silent — see the note on Pairs()).
public sealed class MethodologyKindContractParityTests
{
	// Every {domain, wire-input} pair the methodology-kind document is built from, walked
	// recursively from MethodologyKindDef/-Input down through every nested record. A newly
	// introduced nested Def/Input pair is NOT auto-discovered (the two sides don't share a
	// naming convention uniform enough to derive the pairing by reflection alone — this is the
	// "flat, but honestly incomplete" fallback the task allowed); it must be ADDED here by
	// whoever adds it, or this test stays silent about the new pair. Everything that exists
	// TODAY is covered.
	public static TheoryData<Type, Type> Pairs() => new()
	{
		{ typeof(MethodologyKindDef), typeof(MethodologyKindInput) },
		{ typeof(MethodologyDeliveryDef), typeof(MethodologyDeliveryInput) },
			{ typeof(MethodologyBlocksGateDef), typeof(MethodologyBlocksGateInput) },
		{ typeof(MethodologyLinkConstraintDef), typeof(MethodologyLinkConstraintInput) },
		{ typeof(MethodologyTransitionEffectDef), typeof(MethodologyEffectInput) },
		{ typeof(MethodologyWorkflowDef), typeof(MethodologyWorkflowInput) },
		{ typeof(WorkflowStatus), typeof(MethodologyStatusInput) },
		{ typeof(MethodologyTransitionDef), typeof(MethodologyTransitionInput) },
	};

	[Theory]
	[MemberData(nameof(Pairs))]
	public void InputCarriesEveryDomainField(Type def, Type input)
	{
		var missing = SettableFieldNames(def).Except(SettableFieldNames(input)).ToList();

		missing.Should().BeEmpty(
			$"{input.Name} must carry every field of {def.Name} — rules_upsert/template_upsert "
			+ "REPLACE the whole document, so a domain field absent from the input contract is "
			+ "silently wiped on every edit through MCP (work/mcp-rules-upsert-is-lossy). Missing: "
			+ string.Join(", ", missing));
	}

	// The pair table itself must stay non-trivial — an empty/shrunk TheoryData would make the
	// theory above pass vacuously.
	[Fact]
	public void ThePairTable_CoversTheKnownWireShapes()
	{
		var pairs = Pairs().Select(row => ((Type)row[0]!).Name).ToList();
		pairs.Should().Contain([
			nameof(MethodologyKindDef), nameof(MethodologyDeliveryDef),
			nameof(MethodologyLinkConstraintDef), nameof(MethodologyTransitionEffectDef),
			nameof(MethodologyWorkflowDef), nameof(WorkflowStatus), nameof(MethodologyTransitionDef),
		]);
	}

	// Public settable data — init/set properties only. Excludes get-only COMPUTED members (e.g.
	// MethodologyWorkflowDef.Initial, derived from Statuses[0]) which carry nothing on the wire
	// and have no input-side counterpart to miss.
	static HashSet<string> SettableFieldNames(Type t) =>
		t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
			.Where(p => p.CanRead && p.SetMethod is { IsPublic: true })
			.Select(p => p.Name)
			.ToHashSet(StringComparer.Ordinal);

	// Shape parity (above) proves the FIELDS exist on both sides; it does NOT prove
	// MethodologyWire.ParseDefinition actually WIRES a given field through (someone could add
	// the property to MethodologyKindInput and forget the assignment in ParseDefinition — the
	// reflection theory would stay green while the value still silently drops). This is the
	// value-level counterpart: populate all four previously-missing fields and confirm they
	// survive the exact Input→Def mapping rules_upsert/template_upsert run through.
	[Fact]
	public void ParseDefinition_CarriesTheFourPreviouslyMissingFields()
	{
		var input = new MethodologyDefInput
		{
			Name = "parity-check",
			Kinds =
			[
				new MethodologyKindInput
				{
					Kind = "work",
					Workflows = [new MethodologyWorkflowInput { Types = ["chore"], Statuses = [new MethodologyStatusInput { Slug = "open" }] }],
					AutoWireSpecFrom = "spec",
					Delivery = new MethodologyDeliveryInput { RequiredTypes = ["feature"], DefectTypes = ["bug"] },
					DefaultView = "kanban",
					OutlineReveal = "navigate",
				},
			],
		};

		var def = MethodologyWire.ParseDefinition(input);
		var kind = def.Kinds.Single();

		kind.AutoWireSpecFrom.Should().Be("spec");
		kind.Delivery.Should().NotBeNull();
		kind.Delivery!.RequiredTypes.Should().Equal("feature");
		kind.Delivery.DefectTypes.Should().Equal("bug");
		kind.DefaultView.Should().Be("kanban");
		kind.OutlineReveal.Should().Be("navigate");
	}

	// work/mcp-rules-get-is-lossy-so-the-round-trip-still-destroys — the OUTPUT-side half of
	// the class above. rules_get/template_get are the READ leg of the SAME documented cycle
	// (rules_get → edit → rules_upsert); the wire's own tool descriptions promise the get and
	// upsert shapes match ("same document shape as tasks_methodology_template_get" /
	// MethodologyReference: "the same shape ... rules_get return and rules_upsert accept").
	// The {Def, Input} pairs above proved the WRITE side carries every domain field; they say
	// nothing about the READ side — MethodologyKindView shipped with only 5 of
	// MethodologyKindDef's 9 fields (Delivery/AutoWireSpecFrom/DefaultView/OutlineReveal
	// missing) for exactly this reason: nothing was asserting {Def, View} parity.
	public static TheoryData<Type, Type> ViewPairs() => new()
	{
		{ typeof(MethodologyKindDef), typeof(MethodologyKindView) },
		{ typeof(MethodologyDeliveryDef), typeof(MethodologyDeliveryView) },
			{ typeof(MethodologyBlocksGateDef), typeof(MethodologyBlocksGateView) },
		{ typeof(MethodologyLinkConstraintDef), typeof(MethodologyLinkConstraintView) },
		{ typeof(MethodologyTransitionEffectDef), typeof(MethodologyEffectView) },
		{ typeof(MethodologyWorkflowDef), typeof(MethodologyWorkflowBlockView) },
		{ typeof(WorkflowStatus), typeof(WorkflowStatusView) },
		{ typeof(MethodologyTransitionDef), typeof(MethodologyTransitionView) },
	};

	[Theory]
	[MemberData(nameof(ViewPairs))]
	public void ViewCarriesEveryDomainField(Type def, Type view)
	{
		var missing = SettableFieldNames(def).Except(SettableFieldNames(view)).ToList();

		missing.Should().BeEmpty(
			$"{view.Name} must carry every field of {def.Name} — rules_get/template_get feed "
			+ "the documented read-edit-write cycle (rules_get → edit → rules_upsert), so a "
			+ "domain field absent from the view contract is invisible to whoever builds the "
			+ "next upsert from this output, and is silently wiped on the very next honest edit "
			+ "(work/mcp-rules-get-is-lossy-so-the-round-trip-still-destroys). Missing: "
			+ string.Join(", ", missing));
	}

	// Same non-vacuity guard as ThePairTable_CoversTheKnownWireShapes, for the view pairs.
	[Fact]
	public void TheViewPairTable_CoversTheKnownWireShapes()
	{
		var pairs = ViewPairs().Select(row => ((Type)row[0]!).Name).ToList();
		pairs.Should().Contain([
			nameof(MethodologyKindDef), nameof(MethodologyDeliveryDef),
			nameof(MethodologyLinkConstraintDef), nameof(MethodologyTransitionEffectDef),
			nameof(MethodologyWorkflowDef), nameof(WorkflowStatus), nameof(MethodologyTransitionDef),
		]);
	}

	// The honest end of the class: shape parity (above) proves the FIELDS exist on both
	// {Def, Input} and {Def, View} — it does NOT prove the two directions COMPOSE to an
	// identity. A field could exist on both sides yet still get dropped or reshaped
	// specifically in ProjectDefinition (the Def→View projector) or ParseDefinition (the
	// Input→Def parser), and the pair-table theories above would stay green throughout,
	// because they inspect field NAMES via reflection, never actual VALUES flowing through
	// the wire. This test drives an unedited rules_get → rules_upsert cycle for a document
	// where every one of MethodologyKindDef's 9 fields is populated, through the ACTUAL wire
	// JSON (MethodologyWire.WireOptions — the same camelCase/omit-nulls posture the MCP SDK
	// serializes every tool call with), and asserts the result equals the original. This
	// catches lossiness on EITHER side with one assertion, independent of the hand-maintained
	// pair table.
	[Fact]
	public void RulesGet_RoundTrip_Through_RulesUpsert_IsIdentity()
	{
		var original = new MethodologyDefinition("roundtrip-quartet",
		[
			new MethodologyKindDef(
				"work", QuickAddAllowed: false,
				Workflows:
				[
					new MethodologyWorkflowDef(
						Types: ["feature", "bug"],
						Statuses:
						[
							new WorkflowStatus("open", "Open", StatusKind.Open, Description: "Not started yet."),
							new WorkflowStatus("done", "Done", StatusKind.TerminalOk),
						],
						Transitions:
						[
							new MethodologyTransitionDef(
								"open", "done", RequiresApproval: true, RequiresReason: true, PreconditionArtifact: "spec_plan")
							{
								EnforceApproval = true,
								Checklist = ["reviewed"],
								Description = "The maintainer's own call.",
							},
						]),
				])
			{
				LinkConstraints = [new MethodologyLinkConstraintDef("feature", "task_spec") { TargetKind = "spec", TargetStatuses = ["accepted"], Description = "Every feature traces to a spec." }],
				Effects =
				[
					new MethodologyTransitionEffectDef("done", "blocks", "incoming", "unblocked", "blocked", Description: "Releases whatever this was blocking."),
					// Effect.onLeave + the Set-omitted pure-consume shape (methodology-blocks-gate-data)
					// must round-trip too — a nullable field is exactly where a lossy wire silently
					// coerces null to "" instead of carrying it through.
					new MethodologyTransitionEffectDef("open", "blocks", "incoming", null, OnLeave: true),
				],
				AutoWireSpecFrom = "spec",
				Delivery = new MethodologyDeliveryDef(RequiredTypes: ["feature"], DefectTypes: ["bug"]),
				DefaultView = "kanban",
				OutlineReveal = "navigate",
				Singleton = true,
				BlocksGate = new MethodologyBlocksGateDef("open", "done"),
				Description = "The work board's kind.",
			},
		]);

		// rules_get side: project the stored definition onto the wire View — exactly what
		// TasksTools.RulesGet hands back to the caller.
		var view = MethodologyWire.ProjectDefinition(original, version: 1, created: null, updated: null);
		var json = JsonSerializer.Serialize(view, MethodologyWire.WireOptions);

		// rules_upsert side: the caller pastes that SAME document back, unedited.
		var input = JsonSerializer.Deserialize<MethodologyDefInput>(json, MethodologyWire.WireOptions);
		var roundTripped = MethodologyWire.ParseDefinition(input!);

		roundTripped.Should().BeEquivalentTo(original);
	}
}
