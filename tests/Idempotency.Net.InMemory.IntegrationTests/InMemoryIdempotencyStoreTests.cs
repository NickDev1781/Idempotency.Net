using Idempotency.Net.Abstractions;
using Idempotency.Net.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Idempotency.Net.InMemory.IntegrationTests;

public sealed class InMemoryIdempotencyStoreTests
{
    [Fact]
    public async Task SaveAndGet_ShouldReturnRecord()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddIdempotency().UseInMemory();
        var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IdempotencyStore>();

        var record = new IdempotencyRecord
        {
            Key = "test-key",
            StatusCode = 200,
            ResponseBody = "ok",
            ContentType = "text/plain",
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
        };

        // Act
        await store.SaveAsync(record);
        var cached = await store.GetAsync("test-key");

        // Assert
        Assert.NotNull(cached);
        Assert.Equal(200, cached.StatusCode);
    }

    [Fact]
    public async Task ExpiredRecord_ShouldReturnNull()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddIdempotency().UseInMemory();
        var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IdempotencyStore>();

        var record = new IdempotencyRecord
        {
            Key = "expired",
            StatusCode = 200,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5)
        };

        await store.SaveAsync(record);
        var cached = await store.GetAsync("expired");
        Assert.Null(cached);
    }
}