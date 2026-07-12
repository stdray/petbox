using System.Text.Json;
using Google.Protobuf;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Resource.V1;
using PetBox.Log.Core.Ingestion;
using PetBox.Web.Ingestion;

namespace PetBox.Tests.Web;

// OtlpLogsParser round-trips, pinning the CLEF-PARITY contract of compat-ingest: a stock OTLP client
// must land the same LogEntryCandidate a Seq/CLEF client does. Two holes are covered here —
// the exception (OTel ships it as exception.{type,message,stacktrace} attributes; CLEF as @x) and the
// message template (OTel .NET ships it as the {OriginalFormat} attribute; CLEF as @mt) — plus the
// documented limit where the template genuinely is not on the wire.
//
// Construction style mirrors OtlpMetricsParserTests: build the proto DTOs, ToByteArray, Parse.
public sealed class OtlpLogsParserTests
{
	static KeyValue Attr(string key, string value) =>
		new() { Key = key, Value = new AnyValue { StringValue = value } };

	const ulong Time = 1_700_000_000_000_000_000UL;

	static byte[] Encode(LogRecord record, params KeyValue[] resourceAttrs)
	{
		var resource = new Resource();
		resource.Attributes.AddRange(resourceAttrs);

		var scopeLogs = new ScopeLogs { Scope = new InstrumentationScope { Name = "test" } };
		scopeLogs.LogRecords.Add(record);

		var resourceLogs = new ResourceLogs { Resource = resource };
		resourceLogs.ScopeLogs.Add(scopeLogs);

		var request = new ExportLogsServiceRequest();
		request.ResourceLogs.Add(resourceLogs);
		return request.ToByteArray();
	}

	static LogRecord Record(string body, params KeyValue[] attrs)
	{
		var record = new LogRecord
		{
			TimeUnixNano = Time,
			SeverityNumber = SeverityNumber.Error,
			Body = new AnyValue { StringValue = body },
		};
		record.Attributes.AddRange(attrs);
		return record;
	}

	static PetBox.Log.Core.Models.LogEntryCandidate ParseOne(byte[] body)
	{
		var result = OtlpLogsParser.Parse(body, "svc");
		result.IsMalformed.Should().BeFalse();
		result.Errors.Should().Be(0);
		return result.Candidates.Should().ContainSingle().Subject;
	}

	static JsonDocument Props(PetBox.Log.Core.Models.LogEntryCandidate c) => JsonDocument.Parse(c.Properties);

	// ── exception ──────────────────────────────────────────────────────────────

	// The OTel .NET SDK sets exception.stacktrace = ex.ToString(), i.e. it ALREADY opens with
	// "Type: message". The composed @x must not double that header.
	[Fact]
	public void ExceptionAttributes_DotNetShape_LiftedVerbatimIntoException()
	{
		const string stack = "System.InvalidOperationException: boom\n   at Foo.Bar() in Foo.cs:line 7";
		var candidate = ParseOne(Encode(Record("request failed",
			Attr("exception.type", "System.InvalidOperationException"),
			Attr("exception.message", "boom"),
			Attr("exception.stacktrace", stack))));

		candidate.Exception.Should().Be(stack);
		candidate.Message.Should().Be("request failed");
	}

	// The exception.* keys are LIFTED, not copied: CLEF does not leave @x in the property bag, and
	// neither do we (they would otherwise land as three normalized attribute columns).
	[Fact]
	public void ExceptionAttributes_AreNotAlsoMirroredIntoProperties()
	{
		var candidate = ParseOne(Encode(Record("boom",
			Attr("exception.type", "System.Exception"),
			Attr("exception.message", "boom"),
			Attr("exception.stacktrace", "System.Exception: boom\n   at X()"),
			Attr("http.route", "/x"))));

		using var props = Props(candidate);
		props.RootElement.TryGetProperty("http.route", out _).Should().BeTrue("unrelated attributes still land");
		props.RootElement.EnumerateObject()
			.Select(p => p.Name)
			.Should().NotContain(n => n.StartsWith("exception", StringComparison.OrdinalIgnoreCase));
	}

	// python/go SDKs put a BARE traceback in exception.stacktrace — no "Type: message" header. The
	// header is then composed in front of it so @x stays self-describing.
	[Fact]
	public void ExceptionAttributes_BareStacktrace_GetsHeaderPrepended()
	{
		var candidate = ParseOne(Encode(Record("boom",
			Attr("exception.type", "ValueError"),
			Attr("exception.message", "bad input"),
			Attr("exception.stacktrace", "Traceback (most recent call last):\n  File \"a.py\", line 1"))));

		candidate.Exception.Should().StartWith("ValueError: bad input");
		candidate.Exception.Should().Contain("Traceback (most recent call last):");
	}

	[Fact]
	public void ExceptionType_Only_StillProducesException()
	{
		var candidate = ParseOne(Encode(Record("boom", Attr("exception.type", "System.Exception"))));

		candidate.Exception.Should().Be("System.Exception");
	}

	[Fact]
	public void NoExceptionAttributes_ExceptionStaysNull()
	{
		var candidate = ParseOne(Encode(Record("all good", Attr("http.route", "/x"))));

		candidate.Exception.Should().BeNull();
	}

	// ── message template ───────────────────────────────────────────────────────

	// The .NET OTLP exporter (IncludeFormattedMessage=true) renders the body and ships the template
	// as the {OriginalFormat} attribute — that is the template, and it must not stay in the bag.
	[Fact]
	public void OriginalFormat_BecomesMessageTemplate_AndBodyStaysMessage()
	{
		var candidate = ParseOne(Encode(Record("Order 42 shipped to Alice",
			Attr("{OriginalFormat}", "Order {OrderId} shipped to {Customer}"),
			Attr("OrderId", "42"))));

		candidate.MessageTemplate.Should().Be("Order {OrderId} shipped to {Customer}");
		candidate.Message.Should().Be("Order 42 shipped to Alice");

		using var props = Props(candidate);
		props.RootElement.EnumerateObject().Select(p => p.Name)
			.Should().NotContain(n => n.Contains("OriginalFormat", StringComparison.Ordinal));
		props.RootElement.GetProperty("OrderId").GetString().Should().Be("42");
	}

	// DOCUMENTED LIMIT: with IncludeFormattedMessage=false the .NET exporter puts the TEMPLATE in the
	// body and sends no {OriginalFormat}; other SDKs have no template concept at all. Nothing on the
	// wire distinguishes those from a plain rendered line, so MessageTemplate == Message == Body.
	// This test exists to pin that we do NOT invent a template (no reverse-engineering of "42" → "{x}").
	[Fact]
	public void NoTemplateAttribute_MessageTemplateFallsBackToBody()
	{
		var candidate = ParseOne(Encode(Record("Order 42 shipped to Alice")));

		candidate.MessageTemplate.Should().Be("Order 42 shipped to Alice");
		candidate.Message.Should().Be("Order 42 shipped to Alice");
	}

	// ── parity with the CLEF path ──────────────────────────────────────────────

	// The whole point of the bug: the same event, emitted by a Seq/CLEF client and by a stock OTLP
	// client, must produce the SAME candidate on the four fidelity-carrying fields.
	[Fact]
	public void OtlpAndClef_SameEvent_ProduceTheSameCandidate()
	{
		const string stack = "System.InvalidOperationException: boom\n   at Foo.Bar()";

		var otlp = ParseOne(Encode(Record("Order 42 failed",
			Attr("{OriginalFormat}", "Order {OrderId} failed"),
			Attr("exception.type", "System.InvalidOperationException"),
			Attr("exception.message", "boom"),
			Attr("exception.stacktrace", stack))));

		var clefLine = JsonSerializer.Serialize(new Dictionary<string, object>
		{
			["@t"] = "2023-11-14T22:13:20.0000000Z",
			["@l"] = "Error",
			["@m"] = "Order 42 failed",
			["@mt"] = "Order {OrderId} failed",
			["@x"] = stack,
		});
		var clef = CleFParser.ParseLine(clefLine, 1).Event!;

		otlp.Message.Should().Be(clef.Message);
		otlp.MessageTemplate.Should().Be(clef.MessageTemplate);
		otlp.Exception.Should().Be(clef.Exception);
		otlp.Level.Should().Be(clef.Level);
	}
}
