using Idempotency.Net.Abstractions;
using Idempotency.Net.Extensions;
using Idempotency.Net.Redis.IntegrationTests.Fixtures;

using Microsoft.Extensions.DependencyInjection;

using StackExchange.Redis;

namespace Idempotency.Net.Redis.IntegrationTests;

public sealed class RedisIdempotencyStoreTests : IClassFixture<RedisContainerFixture>
{
    private static readonly TimeSpan DefaultRecordTtl = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ShortLivedRecordTtl = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan ExpirationWait = TimeSpan.FromSeconds(2);

    private readonly RedisContainerFixture _fixture;

    public RedisIdempotencyStoreTests(RedisContainerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task SaveAsync_ThenGetAsync_WithConnectionString_ReturnsPersistedRecord()
    {
        // Arrange
        const int database = 0;
        string keyPrefix = BuildKeyPrefix("save-get");
        string requestKey = BuildRequestKey();

        await _fixture.FlushDatabaseAsync(database);

        await using ServiceProvider provider = BuildProvider(
            database,
            keyPrefix,
            RedisConfigurationMode.ConnectionString);

        IdempotencyRecord expected = CreateRecord(requestKey, DefaultRecordTtl);

        // Act
        await SaveAsync(provider, expected);
        IdempotencyRecord? actual = await ReadAsync(provider, requestKey);

        // Assert
        Assert.NotNull(actual);
        Assert.Equal(expected.Key, actual.Key);
        Assert.Equal(expected.StatusCode, actual.StatusCode);
        Assert.Equal(expected.ResponseBody, actual.ResponseBody);
        Assert.Equal(expected.ContentType, actual.ContentType);
        Assert.NotNull(actual.ExpiresAt);
        Assert.True(actual.ExpiresAt > actual.CreatedAt);
    }

    [Fact]
    public async Task SaveAsync_ThenGetAsync_WithConfigurationProperty_ReturnsPersistedRecord()
    {
        // Arrange
        const int database = 1;
        string keyPrefix = BuildKeyPrefix("configuration");
        string requestKey = BuildRequestKey();

        await _fixture.FlushDatabaseAsync(database);

        await using ServiceProvider provider = BuildProvider(
            database,
            keyPrefix,
            RedisConfigurationMode.Configuration);

        IdempotencyRecord expected = CreateRecord(requestKey, DefaultRecordTtl);

        // Act
        await SaveAsync(provider, expected);
        IdempotencyRecord? actual = await ReadAsync(provider, requestKey);

        // Assert
        Assert.NotNull(actual);
        Assert.Equal(expected.Key, actual.Key);
        Assert.Equal(expected.StatusCode, actual.StatusCode);
        Assert.Equal(expected.ResponseBody, actual.ResponseBody);
        Assert.Equal(expected.ContentType, actual.ContentType);
        Assert.NotNull(actual.ExpiresAt);
        Assert.True(actual.ExpiresAt > actual.CreatedAt);
    }

    [Fact]
    public async Task GetAsync_AfterExpiration_ReturnsNull()
    {
        // Arrange
        const int database = 2;
        string keyPrefix = BuildKeyPrefix("expiration");
        string requestKey = BuildRequestKey();

        await _fixture.FlushDatabaseAsync(database);

        await using ServiceProvider provider = BuildProvider(
            database,
            keyPrefix,
            RedisConfigurationMode.ConnectionString);

        IdempotencyRecord record = CreateRecord(requestKey, ShortLivedRecordTtl);

        // Act
        await SaveAsync(provider, record);
        await Task.Delay(ExpirationWait);

        IdempotencyRecord? cached = await ReadAsync(provider, requestKey);

        // Assert
        Assert.Null(cached);
    }

    [Fact]
    public async Task SaveAsync_WithInstanceNameAndKeyPrefix_WritesExpectedRedisKey()
    {
        // Arrange
        const int database = 3;
        string keyPrefix = BuildKeyPrefix("keys");
        string instanceName = $"instance-{Guid.NewGuid():N}";
        string requestKey = BuildRequestKey();

        await _fixture.FlushDatabaseAsync(database);

        await using ServiceProvider provider = BuildProvider(
            database,
            keyPrefix,
            RedisConfigurationMode.ConnectionString,
            instanceName: instanceName);

        IdempotencyRecord record = CreateRecord(requestKey, DefaultRecordTtl);

        // Act
        await SaveAsync(provider, record);

        IDatabase db = _fixture.Connection.GetDatabase(database);
        string expectedRedisKey = $"{instanceName}:{keyPrefix}{requestKey}";

        bool exists = await db.KeyExistsAsync(expectedRedisKey);

        // Assert
        Assert.True(exists);
    }

    [Fact]
    public async Task SaveAsync_WithExpiredRecord_DoesNotPersistAndDeletesKey()
    {
        // Arrange
        const int database = 7;
        string keyPrefix = BuildKeyPrefix("ttl-expired");
        string requestKey = BuildRequestKey();
        await _fixture.FlushDatabaseAsync(database);

        await using var provider = BuildProvider(database, keyPrefix, RedisConfigurationMode.ConnectionString);
        var store = provider.GetRequiredService<IdempotencyStore>();

        var expiredRecord = new IdempotencyRecord
        {
            Key = requestKey,
            StatusCode = 200,
            ResponseBody = "{\"result\":\"ok\"}",
            ContentType = "application/json; charset=utf-8",
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5) 
        };

        // Act
        await store.SaveAsync(expiredRecord);

        // Assert
        var mux = _fixture.Connection;
        var db = mux.GetDatabase(database);
        var exists = await db.KeyExistsAsync(keyPrefix + requestKey);
        Assert.False(exists);
    }

    private ServiceProvider BuildProvider(
        int database,
        string keyPrefix,
        RedisConfigurationMode configurationMode,
        string? instanceName = null)
    {
        ServiceCollection services = new();

        services
            .AddIdempotency()
            .UseRedis(options =>
            {
                options.Database = database;
                options.KeyPrefix = keyPrefix;
                options.InstanceName = instanceName;
                options.AbortOnConnectFail = false;

                if (configurationMode == RedisConfigurationMode.ConnectionString)
                {
                    options.ConnectionString = _fixture.ConnectionString;
                    options.Configuration = null;
                }
                else
                {
                    options.ConnectionString = null;
                    options.Configuration = _fixture.ConnectionString;
                }
            });

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

    private static string BuildKeyPrefix(string scenario)
    {
        return $"{scenario}:{Guid.NewGuid():N}:";
    }

    private static string BuildRequestKey()
    {
        return $"request:{Guid.NewGuid():N}";
    }

    private static IdempotencyRecord CreateRecord(string key, TimeSpan ttl)
    {
        DateTimeOffset createdAt = DateTimeOffset.UtcNow;

        return new IdempotencyRecord
        {
            Key = key,
            StatusCode = 201,
            ResponseBody = "{\"result\":\"created\"}",
            ContentType = "application/json; charset=utf-8",
            CreatedAt = createdAt,
            ExpiresAt = createdAt.Add(ttl),
        };
    }

    private enum RedisConfigurationMode
    {
        ConnectionString,
        Configuration,
    }
}