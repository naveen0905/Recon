using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace ReconPlatform.Storage;

/// <summary>
/// Executes read-only T-SQL against Synapse Serverless SQL, which exposes
/// Parquet files in Blob Storage as external tables.
///
/// All queries are parameterized — callers supply a query template and a
/// parameter dictionary. The results are returned as a sequence of row
/// dictionaries so upstream code stays schema-agnostic.
///
/// Authentication: managed identity (connection string must not contain a password).
/// </summary>
public sealed class SynapseClient
{
    private readonly string _connectionString;
    private readonly ILogger<SynapseClient> _logger;

    public SynapseClient(string connectionString, ILogger<SynapseClient> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(logger);

        _connectionString = connectionString;
        _logger = logger;
    }

    /// <summary>
    /// Executes a T-SQL query and returns each row as a column-name → value dictionary.
    /// <paramref name="parameters"/> are added as named SqlParameters (@name → value).
    /// </summary>
    public async IAsyncEnumerable<Dictionary<string, object?>> QueryAsync(
        string sql,
        IReadOnlyDictionary<string, object?>? parameters = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        _logger.LogInformation("Synapse query starting");

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 300; // Synapse queries can be slow on cold start

        if (parameters is not null)
        {
            foreach (var (name, value) in parameters)
                cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        var columnNames = Enumerable.Range(0, reader.FieldCount)
            .Select(reader.GetName)
            .ToArray();

        var rowCount = 0;
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var row = new Dictionary<string, object?>(columnNames.Length, StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < columnNames.Length; i++)
                row[columnNames[i]] = reader.IsDBNull(i) ? null : reader.GetValue(i);

            rowCount++;
            yield return row;
        }

        _logger.LogInformation("Synapse query returned {RowCount} rows", rowCount);
    }

    /// <summary>
    /// Convenience overload for queries with no parameters.
    /// </summary>
    public IAsyncEnumerable<Dictionary<string, object?>> QueryAsync(
        string sql, CancellationToken ct = default) =>
        QueryAsync(sql, null, ct);
}
