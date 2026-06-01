using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReconPlatform.Storage;
using Xunit;

namespace ReconPlatform.UnitTests.Storage;

/// <summary>
/// SqlMetadataClient unit tests.
/// We don't call real SQL — we test guard clauses and construction.
/// Integration tests with a localdb/Testcontainers instance are in Phase 6.
/// </summary>
public class SqlMetadataClientTests
{
    private static SqlMetadataClient MakeClient() =>
        new("Server=localhost;Database=recon;Authentication=Active Directory Managed Identity;",
            NullLogger<SqlMetadataClient>.Instance);

    // ── construction guard clauses ────────────────────────────────────────────

    [Fact]
    public void Constructor_EmptyConnectionString_ThrowsArgument()
    {
        var act = () => new SqlMetadataClient("", NullLogger<SqlMetadataClient>.Instance);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_NullLogger_ThrowsArgumentNull()
    {
        var act = () => new SqlMetadataClient("Server=x", null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_ValidArgs_DoesNotThrow()
    {
        var act = () => MakeClient();
        act.Should().NotThrow();
    }

    // ── UpsertTeamConfigAsync guard clauses ───────────────────────────────────

    [Fact]
    public async Task UpsertTeamConfigAsync_EmptyTeam_ThrowsArgument()
    {
        var client = MakeClient();
        var act = async () => await client.UpsertTeamConfigAsync("", "yaml: true", "user@x.com");
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task UpsertTeamConfigAsync_EmptyActor_ThrowsArgument()
    {
        var client = MakeClient();
        var act = async () => await client.UpsertTeamConfigAsync("team", "yaml: true", "");
        await act.Should().ThrowAsync<ArgumentException>();
    }

    // ── InsertConnectorRunAsync guard clauses ─────────────────────────────────

    [Fact]
    public async Task InsertConnectorRunAsync_EmptyTeam_ThrowsArgument()
    {
        var client = MakeClient();
        var act = async () => await client.InsertConnectorRunAsync("", "src", 0, 0, "ok", null);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    // ── GrantPermissionAsync guard clauses ────────────────────────────────────

    [Fact]
    public async Task GrantPermissionAsync_EmptyUpn_ThrowsArgument()
    {
        var client = MakeClient();
        var act = async () => await client.GrantPermissionAsync("", "team", "reader", "admin@x.com");
        await act.Should().ThrowAsync<ArgumentException>();
    }
}
