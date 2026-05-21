using Idempotency.Net.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Idempotency.Net.Redis
{
    public class RedisIdempotencyLock : IIdempotencyLock
    {
        private readonly IConnectionMultiplexer _connection;
        private readonly RedisIdempotencyOptions _options;

        private readonly Dictionary<string, string> _lockTokens = new();

        public RedisIdempotencyLock(IConnectionMultiplexer connection, RedisIdempotencyOptions options)
        {
            _connection = connection;
            _options = options;
        }

        public async Task<bool> AcquireAsync(string key, CancellationToken cancellation = default)
        {
            var db = _connection.GetDatabase(_options.Database);
            var lockKey = "lock:" + key;
            var lockToken = Guid.NewGuid().ToString();

            bool acquired = await db.LockTakeAsync(lockKey, lockToken, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            if (acquired)
            {
                lock (_lockTokens)
                {
                    _lockTokens[key] = lockToken;
                }    
            }
            return acquired;
        }

        public async Task ReleaseAsync(string key)
        {
            string? lockToken;
            lock (_lockTokens)
            {
                _lockTokens.Remove(key, out lockToken);
            }

            if (lockToken is not null)
            {
                var db = _connection.GetDatabase(_options.Database);
                var lockKey = "lock: " + key;
                await db.LockReleaseAsync(lockKey, lockToken).ConfigureAwait(false);
            }
        }
    }
}
