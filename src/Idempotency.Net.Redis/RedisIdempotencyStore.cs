using System.Text.Json;

using Idempotency.Net.Abstractions;

using Microsoft.Extensions.Options;

using StackExchange.Redis;

namespace Idempotency.Net.Redis;

internal sealed class RedisIdempotencyStore : IdempotencyStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IConnectionMultiplexer _connection;
    private readonly RedisIdempotencyOptions _options;

    public RedisIdempotencyStore(
        IConnectionMultiplexer connection,
        IOptions<RedisIdempotencyOptions> options)
    {
        _connection = connection;
        _options = options.Value;
    }

    public async Task<IdempotencyRecord?> GetAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        IDatabase db = _connection.GetDatabase(_options.Database);
        RedisValue payload = await db.StringGetAsync(BuildRedisKey(key)).ConfigureAwait(false);

        if (payload.IsNullOrEmpty)
            return null;

        IdempotencyRecord? record = JsonSerializer.Deserialize<IdempotencyRecord>(payload.ToString(), SerializerOptions);


        if (record is not null && record.ExpiresAt is not null && record.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            await db.KeyDeleteAsync(BuildRedisKey(key)).ConfigureAwait(false);
            return null;
        }

        return record;
    }

    public async Task SaveAsync(
        IdempotencyRecord record,
        CancellationToken cancellationToken = default)
    {
        IDatabase db = _connection.GetDatabase(_options.Database);
        string redisKey = BuildRedisKey(record.Key);

        if  (record.ExpiresAt is not null && record.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            await db.KeyDeleteAsync(redisKey).ConfigureAwait(false);
            return;
        }

        string payload = JsonSerializer.Serialize(record, SerializerOptions);


        TimeSpan? expiry = null;
        if (record.ExpiresAt is not null)
        {
            TimeSpan remainingTtl = record.ExpiresAt.Value - DateTimeOffset.UtcNow;
            expiry = remainingTtl;
        }

        await db.StringSetAsync(
            redisKey,
            payload,
            expiry).ConfigureAwait(false);
    }

    private string BuildRedisKey(string key)
    {
        return string.IsNullOrWhiteSpace(_options.InstanceName)
            ? string.Concat(_options.KeyPrefix, key)
            : string.Concat(_options.InstanceName, ":", _options.KeyPrefix, key);
    }
}