using Idempotency.Net.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using System;
using System.Collections.Generic;
using System.Text;


namespace Idempotency.Net.PostgreSql
{
    public class PostgreSqlIdempotencyLock : IIdempotencyLock, IAsyncDisposable
    {
        private readonly PostgreSqlIdempotencyOptions _options;

        private readonly NpgsqlDataSource _dataSource;

        private readonly Dictionary<string, (NpgsqlConnection Connection, NpgsqlTransaction Transaction)> _locks = new();

        public PostgreSqlIdempotencyLock(IOptions<PostgreSqlIdempotencyOptions> options, NpgsqlDataSource dataSource)
        {
            _options = options.Value;
            _dataSource = dataSource;
        }

        public async Task<bool> AcquireAsync(string key, CancellationToken cancellationToken = default)
        {
            NpgsqlConnection? connection = null;
            NpgsqlTransaction? transaction = null;
            try
            {
                connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
                transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

                using var cmd = new NpgsqlCommand(
                    "SELECT pg_try_advisory_xact_lock(hashtextextended(@key, 0))",
                    connection, transaction);
                cmd.Parameters.AddWithValue("key", key);
                cmd.CommandTimeout = (int)_options.CommandTimeout.TotalSeconds;

                bool acquired = (bool)await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                if (!acquired)
                {
                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    return false;
                }

                lock (_locks)
                {
                    _locks[key] = (connection, transaction);
                }
                return true;
            }
            catch
            {
                if (transaction is not null)
                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                throw;
            }
            finally
            {
                // Если произошла ошибка, и соединение не было сохранено в словарь – закрываем его
                if (connection is not null && (transaction is null || !_locks.ContainsKey(key)))
                    await connection.CloseAsync().ConfigureAwait(false);
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
        public async ValueTask DisposeAsync()
        {
            var keys = _locks.Keys.ToArray();
            foreach (var key in keys)
            {
                (NpgsqlConnection Connection, NpgsqlTransaction Transaction) entry;
                lock (_locks)
                {
                    if (!_locks.Remove(key, out entry))
                        continue;
                }

                try
                {
                    await entry.Transaction.RollbackAsync().ConfigureAwait(false);
                }
                catch { }
                try
                {
                    await entry.Connection.CloseAsync().ConfigureAwait(false);
                }
                catch { }
            }
        }
    }
}
