using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReconPlatform.Config.Models;
using ReconPlatform.Engine;
using Xunit;

namespace ReconPlatform.UnitTests.Engine;

public class NormalizerTests
{
    private static readonly Normalizer Norm = new(NullLogger<Normalizer>.Instance);
    private static readonly DateTimeOffset PulledAt = new(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static SourceConfig Source(FieldMapping? mapping = null) => new()
    {
        Id = "test-src",
        Type = SourceType.RestApi,
        Mapping = mapping,
        Dedup = new DeduplicationConfig { SourcePriority = 50 },
    };

    // ── direct column mapping ─────────────────────────────────────────────────

    [Fact]
    public void Normalize_DirectColumnMapping_ResolvesCoreFields()
    {
        var row = new Dictionary<string, object?>
        {
            ["hostname"] = "api.example.com",
            ["ip_addr"]  = "10.0.0.1",
            ["port_num"] = "443",
            ["svc"]      = "HTTPS",
        };
        var src = Source(new FieldMapping { Host = "hostname", Ip = "ip_addr", Port = "port_num", Service = "svc" });

        var asset = Norm.Normalize(row, "net-sec", src, PulledAt);

        asset.Host.Should().Be("api.example.com");
        asset.Ip.Should().Be("10.0.0.1");
        asset.Port.Should().Be(443);
        asset.Service.Should().Be("HTTPS");
        asset.Team.Should().Be("net-sec");
        asset.SourceId.Should().Be("test-src");
        asset.PulledAt.Should().Be(PulledAt);
        asset.SourcePriority.Should().Be(50);
    }

    [Fact]
    public void Normalize_MissingOptionalField_LeavesNull()
    {
        var row = new Dictionary<string, object?> { ["host"] = "x.com" };
        var src = Source(new FieldMapping { Host = "host" });

        var asset = Norm.Normalize(row, "t", src, PulledAt);

        asset.Ip.Should().BeNull();
        asset.Port.Should().BeNull();
        asset.Severity.Should().BeNull();
    }

    // ── JSONPath mapping ──────────────────────────────────────────────────────

    [Fact]
    public void Normalize_JsonPathMapping_ResolvesNestedField()
    {
        var row = new Dictionary<string, object?>
        {
            ["data"] = new Dictionary<string, object?> { ["hostname"] = "nested.example.com" },
        };
        var src = Source(new FieldMapping { Host = "$.data.hostname" });

        var asset = Norm.Normalize(row, "t", src, PulledAt);

        asset.Host.Should().Be("nested.example.com");
    }

    // ── port parsing ──────────────────────────────────────────────────────────

    [Fact]
    public void Normalize_NonNumericPort_ReturnsNull()
    {
        var row = new Dictionary<string, object?> { ["port"] = "not-a-number" };
        var src = Source(new FieldMapping { Port = "port" });

        var asset = Norm.Normalize(row, "t", src, PulledAt);

        asset.Port.Should().BeNull();
    }

    // ── confidence score ──────────────────────────────────────────────────────

    [Fact]
    public void Normalize_ConfidenceScore_ParsedFromColumn()
    {
        var row = new Dictionary<string, object?> { ["score"] = "0.75" };
        var src = Source(new FieldMapping { ConfidenceScore = "score" });

        var asset = Norm.Normalize(row, "t", src, PulledAt);

        asset.ConfidenceScore.Should().BeApproximately(0.75, 1e-9);
    }

    [Fact]
    public void Normalize_NoConfidenceMapping_DefaultsToOne()
    {
        var row = new Dictionary<string, object?> { ["host"] = "x" };
        var src = Source(new FieldMapping { Host = "host" });

        var asset = Norm.Normalize(row, "t", src, PulledAt);

        asset.ConfidenceScore.Should().Be(1.0);
    }

    // ── identity fields ───────────────────────────────────────────────────────

    [Fact]
    public void Normalize_AssetIdAndDedupKey_LeftEmpty()
    {
        var row = new Dictionary<string, object?> { ["host"] = "x" };
        var src = Source(new FieldMapping { Host = "host" });

        var asset = Norm.Normalize(row, "t", src, PulledAt);

        asset.AssetId.Should().BeEmpty();
        asset.DedupKey.Should().BeEmpty();
        asset.Version.Should().Be(1);
    }

    // ── raw payload ───────────────────────────────────────────────────────────

    [Fact]
    public void Normalize_RawField_IsJsonSerializedRow()
    {
        var row = new Dictionary<string, object?> { ["host"] = "x.com" };
        var src = Source(new FieldMapping { Host = "host" });

        var asset = Norm.Normalize(row, "t", src, PulledAt);

        asset.Raw.Should().NotBeNullOrEmpty();
        asset.Raw.Should().Contain("x.com");
    }

    // ── guard clauses ─────────────────────────────────────────────────────────

    [Fact]
    public void Normalize_NullRow_ThrowsArgumentNull()
    {
        var act = () => Norm.Normalize(null!, "t", Source(), PulledAt);
        act.Should().Throw<ArgumentNullException>();
    }
}
