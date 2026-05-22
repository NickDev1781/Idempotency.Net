using Idempotency.Net.Abstractions;

using Microsoft.Extensions.Options;

using Npgsql;

namespace Idempotency.Net.PostgreSql;

internal sealed class PostgreSqlIdempotencyStore : IdempotencyStore
{
    private readonly PostgreSqlIdempotencyOptions _options;
    private readonly NpgsqlDataSource _dataSource;

    private readonly SemaphoreSlim _tableCreationLock = new(1, 1);
    private volatile bool _tableCreated;

    public PostgreSqlIdempotencyStore(IOptions<PostgreSqlIdempotencyOptions> options, NpgsqlDataSource dataSource)
    {
        _options = options.Value;
        _dataSource = dataSource;
    }

    public async Task<IdempotencyRecord?> GetAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        await EnsureTableAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var sql = $"""
            SELECT status_code, response_body, content_type, created_at, expires_at
            FROM {GetQualifiedTableName()}
            WHERE idempotency_key = @key
            """;

        await using var command = new NpgsqlCommand(sql, connection)
        {
            CommandTimeout = (int)_options.CommandTimeout.TotalSeconds,
        };

        command.Parameters.AddWithValue("key", key);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        DateTimeOffset? expiresAt = reader.IsDBNull(4)
            ? null
            : reader.GetFieldValue<DateTimeOffset>(4);

        return expiresAt is not null && expiresAt <= DateTimeOffset.UtcNow
            ? null
            : new IdempotencyRecord
            {
                Key = key,
                StatusCode = reader.GetInt32(0),
                ResponseBody = reader.IsDBNull(1) ? null : reader.GetString(1),
                ContentType = reader.IsDBNull(2) ? null : reader.GetString(2),
                CreatedAt = reader.GetFieldValue<DateTimeOffset>(3),
                ExpiresAt = expiresAt,
            };
    }

    public async Task SaveAsync(
        IdempotencyRecord record,
        CancellationToken cancellationToken = default)
    {
        await EnsureTableAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var sql = $"""
            INSERT INTO {GetQualifiedTableName()} (
                idempotency_key,
                status_code,
                response_body,
                content_type,
                created_at,
                expires_at
            )
            VALUES (
                @key,
                @status_code,
                @response_body,
                @content_type,
                @created_at,
                @expires_at
            )
            ON CONFLICT (idempotency_key)
            DO UPDATE SET
                status_code = EXCLUDED.status_code,
                response_body = EXCLUDED.response_body,
                content_type = EXCLUDED.content_type,
                created_at = EXCLUDED.created_at,
                expires_at = EXCLUDED.expires_at;
            """;

        await using var command = new NpgsqlCommand(sql, connection)
        {
            CommandTimeout = (int)_options.CommandTimeout.TotalSeconds,
        };

        command.Parameters.AddWithValue("key", record.Key);
        command.Parameters.AddWithValue("status_code", record.StatusCode);
        command.Parameters.AddWithValue("response_body", (object?)record.ResponseBody ?? DBNull.Value);
        command.Parameters.AddWithValue("content_type", (object?)record.ContentType ?? DBNull.Value);
        command.Parameters.AddWithValue("created_at", record.CreatedAt);
        command.Parameters.AddWithValue("expires_at", (object?)record.ExpiresAt ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }


    private async Task EnsureTableAsync(CancellationToken cancellationToken)
    {
        if (!_options.EnableAutoCreateTable || _tableCreated)
            return;

        await _tableCreationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_tableCreated)
                return;

            await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var quotedSchema = QuoteIdentifier(_options.Schema);
            var quotedTable = QuoteIdentifier(_options.TableName);

            string sql = $"""
            CREATE SCHEMA IF NOT EXISTS {quotedSchema};

            CREATE TABLE IF NOT EXISTS {quotedSchema}.{quotedTable} (
                idempotency_key TEXT PRIMARY KEY,
                status_code INTEGER NOT NULL,
                response_body TEXT NULL,
                content_type TEXT NULL,
                created_at TIMESTAMPTZ NOT NULL,
                expires_at TIMESTAMPTZ NULL
            );
            """;

            await using NpgsqlCommand command = new(sql, connection)
            {
                CommandTimeout = (int)_options.CommandTimeout.TotalSeconds,
            };
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            string indexSql = $"""
            CREATE INDEX IF NOT EXISTS idx_{_options.TableName}_expires_at 
                ON {quotedSchema}.{quotedTable} (expires_at) 
                WHERE expires_at IS NOT NULL;
            """;

            await using NpgsqlCommand indexCommand = new(indexSql, connection)
            {
                CommandTimeout = (int)_options.CommandTimeout.TotalSeconds,
            };
            await indexCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            _tableCreated = true;
        }
        finally
        {
            _tableCreationLock.Release();
        }
    }

    private string GetQualifiedTableName()
    {
        return $"{QuoteIdentifier(_options.Schema)}.{QuoteIdentifier(_options.TableName)}";
    }

    private static string QuoteIdentifier(string identifier)
    {
        return $"\"{identifier.Replace("\"", "\"\"")}\"";
    }
}