using Idempotency.Net.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using System;
using System.Collections.Generic;
using System.Text;


namespace Idempotency.Net.PostgreSql
{
    public class PostgreSqlIdempotencyLock: IIdempotencyLock
    {
        private readonly PostgreSqlIdempotencyOptions _options;

        private readonly Dictionary<string, (NpgsqlConnection Connection, NpgsqlTransaction Transaction)> _locks = new();

        public PostgreSqlIdempotencyLock(IOptions<PostgreSqlIdempotencyOptions> options)
        {
            _options = options.Value;
        }

        public async Task<bool> AcquireAsync(string key, CancellationToken cancellationToken = default)
        {
            var connection = new NpgsqlConnection(_options.ConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var transaction = await connection.BeginTransactionAsync();

            using var cmd = new NpgsqlCommand(
                "SELECT pg_try_advisory_xact_lock(hashtextextended(@key, 0))",
                connection, transaction);
            cmd.Parameters.AddWithValue("key", key);
            cmd.CommandTimeout = (int)_options.CommandTimeout.TotalSeconds;

            bool acquired = (bool)await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

            if (acquired)
            {
                lock (_locks)
                {
                    _locks[key] = (connection, transaction);
                }
                return true;
            }
            else
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                await connection.CloseAsync().ConfigureAwait(false);
                return false;
            }
        }

        public async Task ReleaseAsync(string key)
        {
            (NpgsqlConnection Connection, NpgsqlTransaction Transaction) entry;
            lock (_locks)
            {
                if (!_locks.Remove(key, out entry))
                    return;
            }

            try
            {
                await entry.Transaction.CommitAsync().ConfigureAwait(false);
            }
            catch
            {
                await entry.Transaction.RollbackAsync().ConfigureAwait(false);
                throw;
            }
            finally
            {
                await entry.Connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }
}
