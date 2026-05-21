namespace Idempotency.Net.Abstractions;

/// <summary>
/// Configuration options for the idempotency library.
/// </summary>
public sealed class IdempotencyOptions
{
    /// <summary>
    /// Gets or sets the name of the header containing the idempotency key. Default is "X-Idempotency-Key".
    /// </summary>
    public string HeaderName { get; set; } = "X-Idempotency-Key";

    /// <summary>
    /// Gets or sets the retention period for stored idempotency records. Default is 24 hours.
    /// </summary>
    public TimeSpan Expiration { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Gets or sets the maximum time to wait for a distributed lock. Default is 5 seconds.
    /// </summary>
    public TimeSpan LockTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets or sets the lifetime of a distributed lock. Default is 10 seconds.
    /// </summary>
    public TimeSpan LockExpiry { get; set; } = TimeSpan.FromSeconds(10);

}