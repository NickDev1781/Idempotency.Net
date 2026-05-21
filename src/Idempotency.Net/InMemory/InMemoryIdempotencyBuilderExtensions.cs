using Idempotency.Net.Abstractions;
using Idempotency.Net.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Idempotency.Net.Extensions;

public static class InMemoryIdempotencyBuilderExtensions
{
    public static IdempotencyBuilder UseInMemory(this IdempotencyBuilder builder)
    {
        builder.Services.AddSingleton<InMemoryIdempotencyStore>();
        builder.Services.AddSingleton<IdempotencyStore>(sp => sp.GetRequiredService<InMemoryIdempotencyStore>());
        builder.Services.TryAddScoped<IIdempotencyLock, InMemoryIdempotencyLock>();
        return builder;
    }
}