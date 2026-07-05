using System.Text.Json;
using Google.Protobuf;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Metrics.V1;
using OpenTelemetry.Proto.Resource.V1;
using PetBox.Log.Core.Metrics;
using PetBox.Web.Ingestion;

namespace PetBox.Tests.Web;

// OtlpMetricsParser round-trips: build an ExportMetricsServiceRequest in memory, serialize to
// protobuf, parse, and assert the resulting MetricPointRecord fields. Covers each of the five point
// types plus the resource-wins attribute merge and malformed-bytes → IsMalformed. Mirrors the
// construction style the traces/logs parser tests use (build proto DTOs, ToByteArray, Parse).
public sealed class OtlpMetricsParserTests
{
	static KeyValue Attr(string key, string value) =>
		new() { Key = key, Value = new AnyValue { StringValue = value } };

	// Wraps a single Metric in the ResourceMetrics → ScopeMetrics envelope with the given
	// resource/scope/point attribute bags already threaded through by the caller.
	static byte[] Encode(Metric metric, params KeyValue[] resourceAttrs)
	{
		var resource = new Resource();
		resource.Attributes.AddRange(resourceAttrs);

		var scopeMetrics = new ScopeMetrics { Scope = new InstrumentationScope { Name = "test" } };
		scopeMetrics.Metrics.Add(metric);

		var resourceMetrics = new ResourceMetrics { Resource = resource };
		resourceMetrics.ScopeMetrics.Add(scopeMetrics);

		var request = new ExportMetricsServiceRequest();
		request.ResourceMetrics.Add(resourceMetrics);
		return request.ToByteArray();
	}

	const ulong Time = 1_700_000_000_000_000_000UL;
	const ulong Start = 1_699_000_000_000_000_000UL;

	[Fact]
	public void Gauge_DoubleArm_RoundTrips()
	{
		var dp = new NumberDataPoint { TimeUnixNano = Time, AsDouble = 0.42 };
		var metric = new Metric { Name = "process.cpu.utilization", Unit = "1", Gauge = new Gauge() };
		metric.Gauge.DataPoints.Add(dp);

		var result = OtlpMetricsParser.Parse(Encode(metric));

		result.IsMalformed.Should().BeFalse();
		result.Errors.Should().Be(0);
		var p = result.Points.Should().ContainSingle().Subject;
		p.MetricName.Should().Be("process.cpu.utilization");
		p.MetricType.Should().Be((int)MetricPointType.Gauge);
		p.Unit.Should().Be("1");
		p.TimeUnixNs.Should().Be((long)Time);
		p.ValueDouble.Should().Be(0.42);
		p.ValueLong.Should().BeNull();
		p.AggregationTemporality.Should().BeNull();
		p.IsMonotonic.Should().BeNull();
	}

	[Fact]
	public void Sum_IntArm_PreservesInt64_AndCarriesTemporalityMonotonic()
	{
		var dp = new NumberDataPoint { TimeUnixNano = Time, StartTimeUnixNano = Start, AsInt = 9223372036854775807L };
		var sum = new Sum
		{
			AggregationTemporality = AggregationTemporality.Cumulative,
			IsMonotonic = true,
		};
		sum.DataPoints.Add(dp);
		var metric = new Metric { Name = "http.server.requests", Sum = sum };

		var result = OtlpMetricsParser.Parse(Encode(metric));

		var p = result.Points.Should().ContainSingle().Subject;
		p.MetricType.Should().Be((int)MetricPointType.Sum);
		p.ValueLong.Should().Be(9223372036854775807L); // int64 exactness, never through double
		p.ValueDouble.Should().BeNull();
		p.StartUnixNs.Should().Be((long)Start);
		p.AggregationTemporality.Should().Be((int)AggregationTemporality.Cumulative);
		p.IsMonotonic.Should().BeTrue();
	}

	[Fact]
	public void Histogram_Aggregates_And_JsonTails_RoundTrip()
	{
		var dp = new HistogramDataPoint { TimeUnixNano = Time, Count = 3, Sum = 12.5, Min = 1.0, Max = 9.0 };
		dp.ExplicitBounds.AddRange(new[] { 1.0, 5.0, 10.0 });
		dp.BucketCounts.AddRange(new ulong[] { 0, 1, 2, 0 });
		var hist = new Histogram { AggregationTemporality = AggregationTemporality.Delta };
		hist.DataPoints.Add(dp);
		var metric = new Metric { Name = "http.server.duration", Histogram = hist };

		var result = OtlpMetricsParser.Parse(Encode(metric));

		var p = result.Points.Should().ContainSingle().Subject;
		p.MetricType.Should().Be((int)MetricPointType.Histogram);
		p.Count.Should().Be(3);
		p.Sum.Should().Be(12.5);
		p.Min.Should().Be(1.0);
		p.Max.Should().Be(9.0);
		p.AggregationTemporality.Should().Be((int)AggregationTemporality.Delta);
		p.ExplicitBoundsJson.Should().Be("[1,5,10]");
		p.BucketCountsJson.Should().Be("[0,1,2,0]");
	}

	[Fact]
	public void ExponentialHistogram_Scalars_And_BucketJson_RoundTrip()
	{
		var dp = new ExponentialHistogramDataPoint
		{
			TimeUnixNano = Time,
			Count = 6,
			Sum = 20.0,
			Min = 0.5,
			Max = 8.0,
			Scale = 2,
			ZeroCount = 1,
			Positive = new ExponentialHistogramDataPoint.Types.Buckets { Offset = 3 },
			Negative = new ExponentialHistogramDataPoint.Types.Buckets { Offset = -2 },
		};
		dp.Positive.BucketCounts.AddRange(new ulong[] { 1, 2 });
		dp.Negative.BucketCounts.AddRange(new ulong[] { 1 });
		var exp = new ExponentialHistogram { AggregationTemporality = AggregationTemporality.Cumulative };
		exp.DataPoints.Add(dp);
		var metric = new Metric { Name = "db.latency", ExponentialHistogram = exp };

		var result = OtlpMetricsParser.Parse(Encode(metric));

		var p = result.Points.Should().ContainSingle().Subject;
		p.MetricType.Should().Be((int)MetricPointType.ExponentialHistogram);
		p.Count.Should().Be(6);
		p.Sum.Should().Be(20.0);
		p.Min.Should().Be(0.5);
		p.Max.Should().Be(8.0);
		p.Scale.Should().Be(2);
		p.ZeroCount.Should().Be(1);
		p.AggregationTemporality.Should().Be((int)AggregationTemporality.Cumulative);
		p.PositiveBucketsJson.Should().Be("""{"offset":3,"bucket_counts":[1,2]}""");
		p.NegativeBucketsJson.Should().Be("""{"offset":-2,"bucket_counts":[1]}""");
	}

	[Fact]
	public void Summary_Count_Sum_And_Quantiles_RoundTrip()
	{
		var dp = new SummaryDataPoint { TimeUnixNano = Time, Count = 10, Sum = 100.0 };
		dp.QuantileValues.Add(new SummaryDataPoint.Types.ValueAtQuantile { Quantile = 0.5, Value = 8.0 });
		dp.QuantileValues.Add(new SummaryDataPoint.Types.ValueAtQuantile { Quantile = 0.99, Value = 42.0 });
		var summary = new Summary();
		summary.DataPoints.Add(dp);
		var metric = new Metric { Name = "rpc.duration", Summary = summary };

		var result = OtlpMetricsParser.Parse(Encode(metric));

		var p = result.Points.Should().ContainSingle().Subject;
		p.MetricType.Should().Be((int)MetricPointType.Summary);
		p.Count.Should().Be(10);
		p.Sum.Should().Be(100.0);
		p.QuantileValuesJson.Should().Be("""[{"quantile":0.5,"value":8},{"quantile":0.99,"value":42}]""");
	}

	[Fact]
	public void AttributeMerge_ResourceWinsOnCollision()
	{
		var dp = new NumberDataPoint { TimeUnixNano = Time, AsDouble = 1.0 };
		dp.Attributes.Add(Attr("env", "dev"));   // point-level
		dp.Attributes.Add(Attr("route", "/x"));  // point-only
		var metric = new Metric { Name = "m", Gauge = new Gauge() };
		metric.Gauge.DataPoints.Add(dp);

		// resource carries env=prod → must win over the point's env=dev.
		var result = OtlpMetricsParser.Parse(Encode(metric, Attr("env", "prod"), Attr("host", "h1")));

		var p = result.Points.Should().ContainSingle().Subject;
		using var doc = JsonDocument.Parse(p.AttributesJson);
		doc.RootElement.GetProperty("env").GetString().Should().Be("prod");
		doc.RootElement.GetProperty("host").GetString().Should().Be("h1");
		doc.RootElement.GetProperty("route").GetString().Should().Be("/x");
	}

	[Fact]
	public void ZeroTimestamp_CountsAsError_AndIsSkipped()
	{
		var dp = new NumberDataPoint { TimeUnixNano = 0, AsDouble = 1.0 }; // required time missing
		var metric = new Metric { Name = "m", Gauge = new Gauge() };
		metric.Gauge.DataPoints.Add(dp);

		var result = OtlpMetricsParser.Parse(Encode(metric));

		result.Points.Should().BeEmpty();
		result.Errors.Should().Be(1);
		result.IsMalformed.Should().BeFalse();
	}

	[Fact]
	public void MalformedBytes_ReturnsMalformed()
	{
		var result = OtlpMetricsParser.Parse(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF });

		result.IsMalformed.Should().BeTrue();
		result.Points.Should().BeEmpty();
		result.Errors.Should().Be(0);
	}
}
