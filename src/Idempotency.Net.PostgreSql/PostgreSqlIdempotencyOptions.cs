namespace Idempotency.Net.PostgreSql;

/// <summary>
/// Configuration options for the PostgreSQL idempotency store and lock.
/// </summary>
public sealed class PostgreSqlIdempotencyOptions
{
    /// <summary>
    /// Gets or sets the PostgreSQL connection string.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Gets or sets the database schema where the idempotency table resides. Default is "public".
    /// </summary>
    public string Schema { get; set; } = "public";

    /// <summary>
    /// Gets or sets the name of the idempotency records table. Default is "idempotency_records".
    /// </summary>
    public string TableName { get; set; } = "idempotency_records";

    /// <summary>
    /// Gets or sets whether to automatically create the table if it does not exist. Default is true.
    /// </summary>
    public bool EnableAutoCreateTable { get; set; } = true;

    /// <summary>
    /// Gets or sets the command timeout for PostgreSQL operations. Default is 30 seconds.
    /// </summary>
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets the maximum number of expired records to delete per cleanup. Set to 0 to disable. Default is 1000.
    /// </summary>
    public int CleanupBatchSize { get; set; } = 1000;

    /// <summary>
    /// Enables background cleanup of expired idempotency records. Default is true.
    /// </summary>
    public bool EnableBackgroundCleanup { get; set; } = true;

    /// <summary>
    /// Interval between background cleanup runs. Default is 5 minutes.
    /// </summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(5);
}