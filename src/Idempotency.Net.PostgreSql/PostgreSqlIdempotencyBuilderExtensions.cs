using Idempotency.Net.Abstractions;
using Idempotency.Net.Extensions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Idempotency.Net.PostgreSql;

public static class PostgreSqlIdempotencyBuilderExtensions
{
    public static IdempotencyBuilder UsePostgreSql(
        this IdempotencyBuilder builder,
        Action<PostgreSqlIdempotencyOptions>? configure = null)
    {
        builder.Services.AddOptions<PostgreSqlIdempotencyOptions>();

        if (configure is not null)
            builder.Services.Configure(configure);

        builder.Services.AddScoped<PostgreSqlIdempotencyStore>();
        builder.Services.AddScoped<IdempotencyStore>(serviceProvider =>
            serviceProvider.GetRequiredService<PostgreSqlIdempotencyStore>());
        builder.Services.TryAddScoped<IIdempotencyLock, PostgreSqlIdempotencyLock>();

        return builder;
    }
}