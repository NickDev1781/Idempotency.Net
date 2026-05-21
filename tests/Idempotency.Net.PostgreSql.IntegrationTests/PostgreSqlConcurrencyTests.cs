using Idempotency.Net.Abstractions;
using Idempotency.Net.Extensions;
using Idempotency.Net.PostgreSql.IntegrationTests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Idempotency.Net.PostgreSql.IntegrationTests;

public sealed class PostgreSqlConcurrencyTests : IClassFixture<PostgreSqlContainerFixture>
{
    private readonly PostgreSqlContainerFixture _fixture;

    public PostgreSqlConcurrencyTests(PostgreSqlContainerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ConcurrentRequests_WithSameKey_ExecuteLogicOnlyOnce()
    {
        // Arrange
        string schema = BuildSchemaName("concurrent");
        string tableName = BuildTableName("records");
        string requestKey = BuildRequestKey();

        await _fixture.RecreateSchemaAsync(schema);

        await using ServiceProvider provider = BuildProvider(schema, tableName);
        var store = provider.GetRequiredService<IdempotencyStore>();
        var lockProvider = provider.GetRequiredService<IIdempotencyLock>();

        int executionCount = 0;

        async Task HandleRequest()
        {
            var cached = await store.GetAsync(requestKey);
            if (cached is not null)
                return;

            bool acquired = await lockProvider.AcquireAsync(requestKey);
            if (!acquired)
            {
                await Task.Delay(50);
                return;
            }

            try
            {
                cached = await store.GetAsync(requestKey);
                if (cached is not null)
                    return;

                Interlocked.Increment(ref executionCount);
                var record = new IdempotencyRecord
                {
                    Key = requestKey,
                    StatusCode = 200,
                    ResponseBody = "{\"result\":\"ok\"}",
                    ContentType = "application/json; charset=utf-8",
                    CreatedAt = DateTimeOffset.UtcNow,
                    ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
                };
                await store.SaveAsync(record);
            }
            finally
            {
                await lockProvider.ReleaseAsync(requestKey);
            }
        }

        // Act
        var tasks = Enumerable.Range(0, 10).Select(_ => Task.Run(HandleRequest));
        await Task.WhenAll(tasks);

        // Assert
        Assert.Equal(1, executionCount);
    }

    private ServiceProvider BuildProvider(string schema, string tableName)
    {
        var services = new ServiceCollection();

        services.AddIdempotency()
                .UsePostgreSql(options =>
                {
                    options.ConnectionString = _fixture.ConnectionString;
                    options.Schema = schema;
                    options.TableName = tableName;
                    options.EnableAutoCreateTable = true;
                    options.CleanupBatchSize = 1000;
                });

        return services.BuildServiceProvider();
    }

    private static string BuildSchemaName(string scenario) => $"{scenario}_{Guid.NewGuid():N}";
    private static string BuildTableName(string scenario) => $"{scenario}_{Guid.NewGuid():N}";
    private static string BuildRequestKey() => $"request:{Guid.NewGuid():N}";
}