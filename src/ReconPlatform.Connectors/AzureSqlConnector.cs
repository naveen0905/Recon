using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using ReconPlatform.Config;
using ReconPlatform.Config.Models;
using ReconPlatform.Connectors.Interfaces;

namespace ReconPlatform.Connectors;

public sealed class AzureSqlConnector : IConnector
{
    public string ConnectorType => "azure_sql";

    private readonly SecretResolver _secretResolver;
    private readonly ILogger<AzureSqlConnector> _logger;

    public AzureSqlConnector(SecretResolver secretResolver, ILogger<AzureSqlConnector> logger)
    {
        _secretResolver = secretResolver;
        _logger = logger;
    }

    public async Task<IEnumerable<Dictionary<string, object>>> PullAsync(
        SourceConfig config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);

        var connectionString = await ResolveConnectionStringAsync(config, ct).ConfigureAwait(false);
        var results = new List<Dictionary<string, object>>();

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var command = new SqlCommand(config.Query, connection);
        command.CommandTimeout = 120;

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var columnCount = reader.FieldCount;
        var columnNames = new string[columnCount];
        for (var i = 0; i < columnCount; i++)
            columnNames[i] = reader.GetName(i);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var row = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < columnCount; i++)
                row[columnNames[i]] = reader.IsDBNull(i) ? string.Empty : reader.GetValue(i);
            results.Add(row);
        }

        _logger.LogInformation(
            "AzureSqlConnector pulled {Count} rows from source {SourceId}",
            results.Count, config.Id);

        return results;
    }

    public async Task<bool> TestConnectionAsync(SourceConfig config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);

        try
        {
            var connectionString = await ResolveConnectionStringAsync(config, ct).ConfigureAwait(false);
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(ct).ConfigureAwait(false);

            await using var command = new SqlCommand("SELECT 1", connection);
            await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            _logger.LogWarning("TestConnectionAsync failed for {SourceId}: {Message}", config.Id, ex.Message);
            return false;
        }
    }

    private async Task<string> ResolveConnectionStringAsync(SourceConfig config, CancellationToken ct)
    {
        if (config.Auth?.Type == AuthType.ManagedIdentity)
        {
            var baseConnStr = string.IsNullOrWhiteSpace(config.ConnectionString)
                ? string.Empty
                : await _secretResolver.ResolveAsync(config.ConnectionString, ct).ConfigureAwait(false);

            // Managed identity auth appended; any existing Authentication keyword is overridden by SqlClient
            return baseConnStr.TrimEnd(';') + ";Authentication=Active Directory Managed Identity";
        }

        if (!string.IsNullOrWhiteSpace(config.ConnectionString))
            return await _secretResolver.ResolveAsync(config.ConnectionString, ct).ConfigureAwait(false);

        throw new InvalidOperationException(
            $"Source '{config.Id}' has no connection string or managed identity auth configured.");
    }
}
