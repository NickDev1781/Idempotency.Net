using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Idempotency.Net.PostgreSql;

internal sealed class BackgroundCleanupService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<PostgreSqlIdempotencyOptions> _options;
    private readonly ILogger<BackgroundCleanupService> _logger;

    public BackgroundCleanupService(
        IServiceScopeFactory scopeFactory,
        IOptions<PostgreSqlIdempotencyOptions> options,
        ILogger<BackgroundCleanupService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = _options.Value;

        if (!options.EnableBackgroundCleanup || options.CleanupInterval <= TimeSpan.Zero)
        {
            _logger.LogInformation("Background cleanup is disabled");
            return;
        }

        _logger.LogInformation("Background cleanup started with interval {Interval}", options.CleanupInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(options.CleanupInterval, stoppingToken);
                await CleanupAsync(options, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during background cleanup");
            }
        }
    }

    private async Task CleanupAsync(PostgreSqlIdempotencyOptions options, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var dataSource = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var qualifiedTable = $"{QuoteIdentifier(options.Schema)}.{QuoteIdentifier(options.TableName)}";

        var sql = $"""
            DELETE FROM {qualifiedTable}
            WHERE expires_at IS NOT NULL AND expires_at <= NOW()
            ORDER BY expires_at
            LIMIT @batch_size
            """;

        await using var command = new NpgsqlCommand(sql, connection)
        {
            CommandTimeout = (int)options.CommandTimeout.TotalSeconds,
        };
        command.Parameters.AddWithValue("batch_size", options.CleanupBatchSize > 0 ? options.CleanupBatchSize : 1000);

        var deleted = await command.ExecuteNonQueryAsync(ct);
        if (deleted > 0)
            _logger.LogInformation("Background cleanup removed {Count} expired records", deleted);
    }

    private static string QuoteIdentifier(string identifier)
    {
        return $"\"{identifier.Replace("\"", "\"\"")}\"";
    }
}