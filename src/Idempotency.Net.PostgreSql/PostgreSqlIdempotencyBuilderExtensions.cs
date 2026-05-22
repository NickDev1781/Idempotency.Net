using Idempotency.Net.Abstractions;
using Idempotency.Net.Extensions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Idempotency.Net.PostgreSql;

/// <summary>
/// Extension methods for configuring PostgreSQL-based idempotency.
/// </summary>
public static class PostgreSqlIdempotencyBuilderExtensions
{
    /// <summary>
    /// Configures the idempotency library to use PostgreSQL as the storage and locking provider.
    /// </summary>
    /// <param name="builder">The <see cref="IdempotencyBuilder"/>.</param>
    /// <param name="configure">Optional delegate to configure <see cref="PostgreSqlIdempotencyOptions"/>.</param>
    /// <returns>The builder for chaining.</returns>
    public static IdempotencyBuilder UsePostgreSql(
        this IdempotencyBuilder builder,
        Action<PostgreSqlIdempotencyOptions>? configure = null)
    {
        builder.Services.AddOptions<PostgreSqlIdempotencyOptions>();

        if (configure is not null)
            builder.Services.Configure(configure);

        builder.Services.AddSingleton(serviceProvider =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<PostgreSqlIdempotencyOptions>>().Value;
            if (string.IsNullOrEmpty(options.ConnectionString)) throw new InvalidOperationException("PostgreSql connection string is required");

            var dataSourceBuilder = new NpgsqlDataSourceBuilder(options.ConnectionString);
            return dataSourceBuilder.Build();
        });
        
        builder.Services.AddScoped<PostgreSqlIdempotencyStore>();
        builder.Services.AddScoped<IdempotencyStore>(serviceProvider =>
            serviceProvider.GetRequiredService<PostgreSqlIdempotencyStore>());
        builder.Services.TryAddScoped<IIdempotencyLock, PostgreSqlIdempotencyLock>();

        builder.Services.AddHostedService<BackgroundCleanupService>();

        return builder;
    }
}