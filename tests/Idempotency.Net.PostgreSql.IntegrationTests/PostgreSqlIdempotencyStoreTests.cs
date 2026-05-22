using Idempotency.Net.Abstractions;
using Idempotency.Net.Extensions;
using Idempotency.Net.PostgreSql.IntegrationTests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Idempotency.Net.PostgreSql.IntegrationTests;

public sealed class PostgreSqlIdempotencyStoreTests : IClassFixture<PostgreSqlContainerFixture>
{
    private static readonly TimeSpan DefaultRecordTtl = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ShortLivedRecordTtl = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan ExpirationWait = TimeSpan.FromSeconds(2);

    private readonly PostgreSqlContainerFixture _fixture;

    public PostgreSqlIdempotencyStoreTests(PostgreSqlContainerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task SaveAsync_ThenGetAsync_ReturnsPersistedRecord()
    {
        // Arrange
        string schema = BuildSchemaName("save_get");
        string tableName = BuildTableName("records");
        string requestKey = BuildRequestKey();

        await _fixture.RecreateSchemaAsync(schema);

        await using ServiceProvider provider = BuildProvider(schema, tableName);

        IdempotencyRecord expected = CreateRecord(requestKey, DefaultRecordTtl);

        // Act
        await SaveAsync(provider, expected);
        IdempotencyRecord? actual = await ReadAsync(provider, requestKey);

        // Assert
        AssertPersistedRecord(expected, actual);
    }

    [Fact]
    public async Task SaveAsync_WithSameKeyTwice_LastWriteWins()
    {
        // Arrange
        string schema = BuildSchemaName("upsert");
        string tableName = BuildTableName("records");
        string requestKey = BuildRequestKey();

        await _fixture.RecreateSchemaAsync(schema);

        await using ServiceProvider provider = BuildProvider(schema, tableName);

        IdempotencyRecord first = CreateRecord(
            requestKey,
            DefaultRecordTtl,
            statusCode: 201,
            responseBody: "{\"result\":\"created\"}");

        IdempotencyRecord updated = CreateRecord(
            requestKey,
            DefaultRecordTtl,
            statusCode: 202,
            responseBody: "{\"result\":\"accepted\"}");

        // Act
        await SaveAsync(provider, first);
        await SaveAsync(provider, updated);
        IdempotencyRecord? actual = await ReadAsync(provider, requestKey);

        // Assert
        AssertPersistedRecord(updated, actual);
    }

    [Fact]
    public async Task GetAsync_AfterExpiration_ReturnsNull()
    {
        // Arrange
        string schema = BuildSchemaName("expiration");
        string tableName = BuildTableName("records");
        string requestKey = BuildRequestKey();

        await _fixture.RecreateSchemaAsync(schema);

        await using ServiceProvider provider = BuildProvider(schema, tableName);

        IdempotencyRecord record = CreateRecord(requestKey, ShortLivedRecordTtl);

        // Act
        await SaveAsync(provider, record);
        await Task.Delay(ExpirationWait);

        IdempotencyRecord? actual = await ReadAsync(provider, requestKey);

        // Assert
        Assert.Null(actual);
    }

    [Fact]
    public async Task SaveAsync_WithAutoCreateTableEnabled_CreatesConfiguredTable()
    {
        // Arrange
        string schema = BuildSchemaName("auto_create");
        string tableName = BuildTableName("records");
        string requestKey = BuildRequestKey();

        await _fixture.DropSchemaIfExistsAsync(schema);

        await using ServiceProvider provider = BuildProvider(
            schema,
            tableName,
            enableAutoCreateTable: true);

        IdempotencyRecord record = CreateRecord(requestKey, DefaultRecordTtl);

        // Act
        await SaveAsync(provider, record);

        bool tableExists = await _fixture.TableExistsAsync(schema, tableName);

        // Assert
        Assert.True(tableExists);
    }

    [Fact]
    public async Task SaveAsync_WithCleanupBatch_RemovesExpiredRows()
    {
        // Arrange
        string schema = BuildSchemaName("cleanup");
        string tableName = BuildTableName("records");
        string expiredKey = BuildRequestKey();

        await _fixture.RecreateSchemaAsync(schema);

        await using ServiceProvider provider = BuildProvider(
            schema,
            tableName,
            cleanupBatchSize: 100);

        // 
        await SaveAsync(provider, CreateRecord(BuildRequestKey(), DefaultRecordTtl));

        await _fixture.InsertRawRecordAsync(
            schema,
            tableName,
            expiredKey,
            statusCode: 200,
            responseBody: "{\"result\":\"expired\"}",
            contentType: "application/json; charset=utf-8",
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-15),
            expiresAt: DateTimeOffset.UtcNow.AddMinutes(-10));

        bool existedBeforeCleanup = await _fixture.RecordExistsInStorageAsync(schema, tableName, expiredKey);
        Assert.True(existedBeforeCleanup);

        // Act
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var dataSource = provider.GetRequiredService<NpgsqlDataSource>();
        var options = provider.GetRequiredService<IOptions<PostgreSqlIdempotencyOptions>>().Value;

        using var scope = scopeFactory.CreateScope();
        await using var connection = await dataSource.OpenConnectionAsync();
        var qualifiedTable = $"{QuoteIdentifier(schema)}.{QuoteIdentifier(tableName)}";

        var sql = $"""
            WITH rows AS (
                SELECT ctid
                FROM {qualifiedTable}
                WHERE expires_at IS NOT NULL AND expires_at <= NOW()
                ORDER BY expires_at
                LIMIT @batch_size
            )
            DELETE FROM {qualifiedTable}
            WHERE ctid IN (SELECT ctid FROM rows);
            """;

        await using var command = new NpgsqlCommand(sql, connection)
        {
            CommandTimeout = (int)options.CommandTimeout.TotalSeconds,
        };
        command.Parameters.AddWithValue("batch_size", options.CleanupBatchSize > 0 ? options.CleanupBatchSize : 1000);
        await command.ExecuteNonQueryAsync();

        // Assert
        bool existsAfterCleanup = await _fixture.RecordExistsInStorageAsync(schema, tableName, expiredKey);
        Assert.False(existsAfterCleanup);
    }

    private static string QuoteIdentifier(string identifier)
    {
        return $"\"{identifier.Replace("\"", "\"\"")}\"";
    }

    private ServiceProvider BuildProvider(
    string schema,
    string tableName,
    bool enableAutoCreateTable = true,
    bool useAdvisoryLocks = true,
    int cleanupBatchSize = 1000,
    bool enableBackgroundCleanup = false,   
    TimeSpan? cleanupInterval = null)     
    {
        ServiceCollection services = new();

        services.AddLogging(); 

        services
            .AddIdempotency()
            .UsePostgreSql(options =>
            {
                options.ConnectionString = _fixture.ConnectionString;
                options.Schema = schema;
                options.TableName = tableName;
                options.EnableAutoCreateTable = enableAutoCreateTable;
                options.CleanupBatchSize = cleanupBatchSize;
                options.EnableBackgroundCleanup = enableBackgroundCleanup;
                options.CleanupInterval = cleanupInterval ?? TimeSpan.FromMinutes(5);
            });


        services.AddSingleton<ILoggerFactory, NullLoggerFactory>();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        return services.BuildServiceProvider();
    }

    private static async Task SaveAsync(ServiceProvider provider, IdempotencyRecord record)
    {
        await using AsyncServiceScope saveScope = provider.CreateAsyncScope();
        IdempotencyStore store = saveScope.ServiceProvider.GetRequiredService<IdempotencyStore>();
        await store.SaveAsync(record);
    }

    private static async Task<IdempotencyRecord?> ReadAsync(ServiceProvider provider, string key)
    {
        await using AsyncServiceScope readScope = provider.CreateAsyncScope();
        IdempotencyStore store = readScope.ServiceProvider.GetRequiredService<IdempotencyStore>();
        return await store.GetAsync(key);
    }

    private static void AssertPersistedRecord(IdempotencyRecord expected, IdempotencyRecord? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.Key, actual.Key);
        Assert.Equal(expected.StatusCode, actual.StatusCode);
        Assert.Equal(expected.ResponseBody, actual.ResponseBody);
        Assert.Equal(expected.ContentType, actual.ContentType);
        Assert.NotNull(actual.ExpiresAt);
        Assert.True(actual.ExpiresAt > actual.CreatedAt);
    }

    private static IdempotencyRecord CreateRecord(
        string key,
        TimeSpan ttl,
        int statusCode = 201,
        string responseBody = "{\"result\":\"created\"}",
        string contentType = "application/json; charset=utf-8")
    {
        DateTimeOffset createdAt = DateTimeOffset.UtcNow;

        return new IdempotencyRecord
        {
            Key = key,
            StatusCode = statusCode,
            ResponseBody = responseBody,
            ContentType = contentType,
            CreatedAt = createdAt,
            ExpiresAt = createdAt.Add(ttl),
        };
    }

    private static string BuildSchemaName(string scenario)
    {
        return $"{scenario}_{Guid.NewGuid():N}";
    }

    private static string BuildTableName(string scenario)
    {
        return $"{scenario}_{Guid.NewGuid():N}";
    }

    private static string BuildRequestKey()
    {
        return $"request:{Guid.NewGuid():N}";
    }
}