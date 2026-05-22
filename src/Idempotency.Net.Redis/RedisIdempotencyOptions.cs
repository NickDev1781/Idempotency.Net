namespace Idempotency.Net.Redis;

/// <summary>
/// Configuration options for the Redis idempotency store and lock.
/// </summary>
public sealed class RedisIdempotencyOptions
{
    /// <summary>
    /// Gets or sets the Redis connection string.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Gets or sets the Redis configuration string (alternative to ConnectionString).
    /// </summary>
    public string? Configuration { get; set; }

    /// <summary>
    /// Gets or sets an optional instance name to prefix all Redis keys.
    /// </summary>
    public string? InstanceName { get; set; }

    /// <summary>
    /// Gets or sets the Redis database index. Default is 0.
    /// </summary>
    public int Database { get; set; }

    /// <summary>
    /// Gets or sets the key prefix for idempotency records. Default is "idempotency:".
    /// </summary>
    public string KeyPrefix { get; set; } = "idempotency:";

    /// <summary>
    /// Gets or sets the connection timeout for Redis. Default is 5 seconds.
    /// </summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets or sets the synchronous timeout for Redis operations. Default is 5 seconds.
    /// </summary>
    public TimeSpan SyncTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets or sets whether to abort on connection failure. Default is false.
    /// </summary>
    public bool AbortOnConnectFail { get; set; }
}