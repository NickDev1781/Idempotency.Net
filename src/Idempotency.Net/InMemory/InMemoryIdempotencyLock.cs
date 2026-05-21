using System.Collections.Concurrent;
using Idempotency.Net.Abstractions;
using Microsoft.Extensions.Options;

namespace Idempotency.Net.InMemory;

internal sealed class InMemoryIdempotencyLock : IIdempotencyLock
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private readonly IdempotencyOptions _options;

    public InMemoryIdempotencyLock(IOptions<IdempotencyOptions> options)
    {
        _options = options.Value;
    }

    public async Task<bool> AcquireAsync(string key, CancellationToken cancellationToken = default)
    {
        var semaphore = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        return await semaphore.WaitAsync(_options.LockTimeout, cancellationToken).ConfigureAwait(false);
    }

    public Task ReleaseAsync(string key)
    {
        if (_locks.TryGetValue(key, out var semaphore))
        {
            semaphore.Release();
        }
        return Task.CompletedTask;
    }
}