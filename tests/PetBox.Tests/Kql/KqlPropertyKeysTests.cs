using System.Text.Json;
using System.Threading.Channels;
using Google.Protobuf;
using Kusto.Language;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Trace.V1;
using PetBox.Log.Core;
using PetBox.Log.Core.Query;
using PetBox.Log.Core.SelfLogging;
using PetBox.Web.Ingestion;
using MelLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace PetBox.Tests.Kql;

// The WRITE-boundary key rules: KqlPropertyKeys.Normalize + NameAllocator (collision policy — first
// original keeps the normalized name, subsequent DISTINCT colliders get '_2', '_3' in encounter
// order), applied by every bag producer: PropertiesJsonSerializer (CLEF/seq events + the self-log),
// and the OTLP parsers' attribute flattening. The search boundary applies the same Normalize rule, so
// hostile-key round-trips resolve end to end.
public sealed class KqlPropertyKeysTests
{
	// --- the collision policy itself ---

	[Fact]
	public void NameAllocator_SuffixesDistinctColliders_InEncounterOrder()
	{
		var names = new KqlPropertyKeys.NameAllocator();
		names.Assign("a\"b").Should().Be("a_b");   // first collider claims the normalized name
		names.Assign("a\\b").Should().Be("a_b_2"); // distinct original → deterministic suffix
		names.Assign("a\tb").Should().Be("a_b_3"); // third distinct collider
		names.Assign("a\"b").Should().Be("a_b");   // SAME original maps stably (last-wins semantics keep working)
		names.Assign("clean.key").Should().Be("clean.key"); // fixed point untouched
	}

	[Fact]
	public void Serializer_CollidingKeys_ProduceSuffixedNames_NoDuplicateJson()
	{
		// Insertion-ordered Dictionary pins the exact encounter-order assignment.
		var bag = new Dictionary<string, object?> { ["a\"b"] = 1, ["a\\b"] = 2 };
		var json = PropertiesJsonSerializer.Serialize(bag);

		using var doc = JsonDocument.Parse(json);
		doc.RootElement.GetProperty("a_b").GetInt32().Should().Be(1);
		doc.RootElement.GetProperty("a_b_2").GetInt32().Should().Be(2);
		doc.RootElement.EnumerateObject().Count().Should().Be(2); // no duplicate keys
	}

	// --- CLEF ingest (the events write boundary) ---

	[Fact]
	public void Clef_CollidingHostileKeys_StoredSuffixed()
	{
		// Raw CLEF keys `a"b` and `a\b` both normalize to a_b; the stored bag must carry BOTH values
		// under distinct names. (CLEF properties pass through an ImmutableDictionary, whose enumeration
		// order — not insertion order — decides which original gets the plain name; assert the SET.)
		var line = """{"@t":"2026-04-19T10:00:00Z","@m":"x","a\"b":1,"a\\b":2}""";
		var parsed = CleFParser.ParseLine(line, 1);
		parsed.IsSuccess.Should().BeTrue();

		using var doc = JsonDocument.Parse(parsed.Event!.Properties);
		var byKey = doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32());
		byKey.Keys.Should().BeEquivalentTo(["a_b", "a_b_2"]);
		byKey.Values.Should().BeEquivalentTo([1, 2]); // neither collider was silently dropped
	}

	// --- OTLP span attributes (the traces write boundary; RepeatedField preserves encounter order) ---

	[Fact]
	public void OtlpTraces_CollidingHostileAttributeKeys_StoredSuffixed()
	{
		var span = new Span
		{
			TraceId = ByteString.CopyFrom(new byte[16] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 }),
			SpanId = ByteString.CopyFrom(new byte[8] { 1, 2, 3, 4, 5, 6, 7, 8 }),
			Name = "op",
			StartTimeUnixNano = 1_000_000_000UL,
			EndTimeUnixNano = 2_000_000_000UL,
		};
		span.Attributes.Add(new KeyValue { Key = "a\"b", Value = new AnyValue { IntValue = 1 } });
		span.Attributes.Add(new KeyValue { Key = "a\\b", Value = new AnyValue { IntValue = 2 } });

		var request = new ExportTraceServiceRequest();
		var resourceSpans = new ResourceSpans();
		var scopeSpans = new ScopeSpans();
		scopeSpans.Spans.Add(span);
		resourceSpans.ScopeSpans.Add(scopeSpans);
		request.ResourceSpans.Add(resourceSpans);

		var result = OtlpTracesParser.Parse(request.ToByteArray());
		result.Spans.Should().ContainSingle();

		using var doc = JsonDocument.Parse(result.Spans[0].AttributesJson);
		doc.RootElement.GetProperty("a_b").GetInt32().Should().Be(1);   // encounter order: first wins the name
		doc.RootElement.GetProperty("a_b_2").GetInt32().Should().Be(2); // second gets the suffix
	}

	// --- the self-log write boundary (#5): SystemLogger routes through PropertiesJsonSerializer ---

	[Fact]
	public void SystemLogger_HostileStateKey_NormalizedAndQueryable()
	{
		var channel = Channel.CreateUnbounded<LogEntryCandidate>();
		var logger = new SystemLogger(
			"PetBox.Test", new SystemLoggerOptions(), channel.Writer, TimeProvider.System);

		var state = new List<KeyValuePair<string, object?>>
		{
			new("we\"ird.key", 42),
			new("{OriginalFormat}", "m"),
		};
		logger.Log(MelLogLevel.Information, default, (IReadOnlyList<KeyValuePair<string, object?>>)state,
			null, (_, _) => "m");

		channel.Reader.TryRead(out var candidate).Should().BeTrue();
		using (var doc = JsonDocument.Parse(candidate!.Properties))
			doc.RootElement.GetProperty("we_ird.key").GetInt32().Should().Be(42); // normalized at write

		// End-to-end: the RAW spelling is queryable — search-side normalization meets the stored form.
		var record = LogEntryRecord.FromCandidate(candidate, 0);
		record.Id = 1;
		var ids = KqlTestHost.Apply(
				[record],
				KustoCode.Parse("events | where Properties[\"we\\\"ird.key\"] == '42'"),
				KqlBackend.Sqlite)
			.Select(r => r.Id).ToList();
		ids.Should().BeEquivalentTo([1L]);
	}
}
