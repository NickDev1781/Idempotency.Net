using System.Collections.Concurrent;
using Idempotency.Net.Abstractions;

namespace Idempotency.Net.InMemory;

internal sealed class InMemoryIdempotencyStore : IdempotencyStore
{
    private readonly ConcurrentDictionary<string, IdempotencyRecord> _records = new();

    public Task<IdempotencyRecord?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        if (_records.TryGetValue(key, out var record))
        {
            if (record.ExpiresAt is not null && record.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                _records.TryRemove(key, out _);
                return Task.FromResult<IdempotencyRecord?>(null);
            }
            return Task.FromResult<IdempotencyRecord?>(record);
        }
        return Task.FromResult<IdempotencyRecord?>(null);
    }

    public Task SaveAsync(IdempotencyRecord record, CancellationToken cancellationToken = default)
    {
        if (record.ExpiresAt is not null && record.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            _records.TryRemove(record.Key, out _);
            return Task.CompletedTask;
        }

        _records[record.Key] = record;
        return Task.CompletedTask;
    }
}