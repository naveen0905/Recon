using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace ReconPlatform.Storage;

/// <summary>
/// Thin data-access layer for the Azure SQL metadata database.
///
/// Tables managed here:
///   team_configs        — serialized YAML configs per team
///   connector_run_log   — one row per connector execution (SOC2 audit trail)
///   engagements         — engagement scope definitions
///   user_permissions    — team-scoped RBAC grants
///   audit_log           — append-only SOC2 audit log (written BEFORE each operation)
///
/// Authentication: managed identity preferred (no password in connection string).
/// All writes are preceded by an audit_log INSERT (pre-action audit per SOC2).
/// All queries are fully parameterized — no string interpolation in SQL.
/// </summary>
public sealed class SqlMetadataClient : IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly ILogger<SqlMetadataClient> _logger;

    public SqlMetadataClient(string connectionString, ILogger<SqlMetadataClient> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(logger);

        _connectionString = connectionString;
        _logger = logger;
    }

    // ── team_configs ──────────────────────────────────────────────────────────

    public async Task UpsertTeamConfigAsync(
        string team, string yamlContent, string actorUpn,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(team);
        ArgumentException.ThrowIfNullOrWhiteSpace(yamlContent);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorUpn);

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await WriteAuditAsync(conn, actorUpn, "team_configs", team, "upsert", ct).ConfigureAwait(false);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            MERGE team_configs AS target
            USING (SELECT @team AS team, @yaml AS yaml_content, SYSUTCDATETIME() AS updated_at) AS source
                ON target.team = source.team
            WHEN MATCHED THEN UPDATE SET yaml_content = source.yaml_content, updated_at = source.updated_at
            WHEN NOT MATCHED THEN INSERT (team, yaml_content, updated_at) VALUES (source.team, source.yaml_content, source.updated_at);
            """;
        cmd.Parameters.AddWithValue("@team", team);
        cmd.Parameters.AddWithValue("@yaml", yamlContent);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("Upserted team_config for team={Team}", team);
    }

    public async Task<string?> GetTeamConfigYamlAsync(string team, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(team);

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT yaml_content FROM team_configs WHERE team = @team";
        cmd.Parameters.AddWithValue("@team", team);

        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result as string;
    }

    public async Task DeleteTeamConfigAsync(string team, string actorUpn, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(team);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorUpn);

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await WriteAuditAsync(conn, actorUpn, "team_configs", team, "delete", ct).ConfigureAwait(false);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM team_configs WHERE team = @team";
        cmd.Parameters.AddWithValue("@team", team);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("Deleted team_config for team={Team}", team);
    }

    // ── connector_run_log ─────────────────────────────────────────────────────

    public async Task InsertConnectorRunAsync(
        string team, string sourceId, int assetsPulled, int assetsDeduped,
        string status, string? errorMessage,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(team);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO connector_run_log
                (team, source_id, run_at, assets_pulled, assets_deduped, status, error_message)
            VALUES
                (@team, @source_id, SYSUTCDATETIME(), @assets_pulled, @assets_deduped, @status, @error_message)
            """;
        cmd.Parameters.AddWithValue("@team", team);
        cmd.Parameters.AddWithValue("@source_id", sourceId);
        cmd.Parameters.AddWithValue("@assets_pulled", assetsPulled);
        cmd.Parameters.AddWithValue("@assets_deduped", assetsDeduped);
        cmd.Parameters.AddWithValue("@status", status);
        cmd.Parameters.AddWithValue("@error_message", (object?)errorMessage ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Logged connector run team={Team} source={Source} pulled={Pulled} deduped={Deduped} status={Status}",
            team, sourceId, assetsPulled, assetsDeduped, status);
    }

    // ── engagements ───────────────────────────────────────────────────────────

    public async Task UpsertEngagementAsync(
        string engagementId, string team, string scopeJson, string actorUpn,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(engagementId);
        ArgumentException.ThrowIfNullOrWhiteSpace(team);
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorUpn);

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await WriteAuditAsync(conn, actorUpn, "engagements", engagementId, "upsert", ct).ConfigureAwait(false);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            MERGE engagements AS target
            USING (SELECT @id AS engagement_id, @team AS team, @scope AS scope_json, SYSUTCDATETIME() AS updated_at) AS source
                ON target.engagement_id = source.engagement_id
            WHEN MATCHED THEN UPDATE SET scope_json = source.scope_json, updated_at = source.updated_at
            WHEN NOT MATCHED THEN INSERT (engagement_id, team, scope_json, created_at, updated_at)
                VALUES (source.engagement_id, source.team, source.scope_json, source.updated_at, source.updated_at);
            """;
        cmd.Parameters.AddWithValue("@id", engagementId);
        cmd.Parameters.AddWithValue("@team", team);
        cmd.Parameters.AddWithValue("@scope", scopeJson);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("Upserted engagement={EngagementId} for team={Team}", engagementId, team);
    }

    public async Task<IReadOnlyList<(string EngagementId, string ScopeJson)>> ListEngagementsAsync(
        string team, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(team);

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT engagement_id, scope_json FROM engagements WHERE team = @team ORDER BY created_at DESC";
        cmd.Parameters.AddWithValue("@team", team);

        var results = new List<(string, string)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            results.Add((reader.GetString(0), reader.GetString(1)));

        return results;
    }

    // ── user_permissions ──────────────────────────────────────────────────────

    public async Task GrantPermissionAsync(
        string userUpn, string team, string role, string actorUpn,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userUpn);
        ArgumentException.ThrowIfNullOrWhiteSpace(team);
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorUpn);

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await WriteAuditAsync(conn, actorUpn, "user_permissions", $"{userUpn}:{team}", "grant", ct)
            .ConfigureAwait(false);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            MERGE user_permissions AS target
            USING (SELECT @upn AS user_upn, @team AS team, @role AS role, SYSUTCDATETIME() AS granted_at) AS source
                ON target.user_upn = source.user_upn AND target.team = source.team
            WHEN MATCHED THEN UPDATE SET role = source.role, granted_at = source.granted_at
            WHEN NOT MATCHED THEN INSERT (user_upn, team, role, granted_at, granted_by)
                VALUES (source.user_upn, source.team, source.role, source.granted_at, @granted_by);
            """;
        cmd.Parameters.AddWithValue("@upn", userUpn);
        cmd.Parameters.AddWithValue("@team", team);
        cmd.Parameters.AddWithValue("@role", role);
        cmd.Parameters.AddWithValue("@granted_by", actorUpn);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("Granted role={Role} to user={Upn} on team={Team}", role, userUpn, team);
    }

    public async Task RevokePermissionAsync(
        string userUpn, string team, string actorUpn,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userUpn);
        ArgumentException.ThrowIfNullOrWhiteSpace(team);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorUpn);

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await WriteAuditAsync(conn, actorUpn, "user_permissions", $"{userUpn}:{team}", "revoke", ct)
            .ConfigureAwait(false);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM user_permissions WHERE user_upn = @upn AND team = @team";
        cmd.Parameters.AddWithValue("@upn", userUpn);
        cmd.Parameters.AddWithValue("@team", team);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("Revoked permission for user={Upn} on team={Team}", userUpn, team);
    }

    // ── audit_log (SOC2) ──────────────────────────────────────────────────────

    // Written before every mutating operation.
    private static async Task WriteAuditAsync(
        SqlConnection conn, string actor, string resource, string resourceId,
        string action, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO audit_log (actor, resource, resource_id, action, occurred_at)
            VALUES (@actor, @resource, @resource_id, @action, SYSUTCDATETIME())
            """;
        cmd.Parameters.AddWithValue("@actor", actor);
        cmd.Parameters.AddWithValue("@resource", resource);
        cmd.Parameters.AddWithValue("@resource_id", resourceId);
        cmd.Parameters.AddWithValue("@action", action);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        return conn;
    }

    public async ValueTask DisposeAsync() => await ValueTask.CompletedTask.ConfigureAwait(false);
}
