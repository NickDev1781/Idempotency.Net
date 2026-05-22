using System.Collections.Concurrent;
using Idempotency.Net.Abstractions;
using Microsoft.Extensions.Options;

namespace Idempotency.Net.InMemory;

internal sealed class InMemoryIdempotencyLock : IIdempotencyLock
{
    private readonly ConcurrentDictionary<string, byte> _locks = new();

    public InMemoryIdempotencyLock(IOptions<IdempotencyOptions> options)
    {
    }

    public Task<bool> AcquireAsync(string key, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_locks.TryAdd(key, 0));
    }

    public Task ReleaseAsync(string key)
    {
        _locks.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}