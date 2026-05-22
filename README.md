# IdempotencyToolkit

**A lightweight, production‑ready idempotency library for ASP.NET Core with built‑in distributed locking and pluggable storage (Redis, PostgreSQL, In‑Memory).**

[![NuGet](https://img.shields.io/nuget/v/IdempotencyToolkit.svg)](https://www.nuget.org/packages/IdempotencyToolkit)
[![Build](https://github.com/NickDev1781/IdempotencyToolKit/actions/workflows/build.yml/badge.svg)](https://github.com/NickDev1781/IdempotencyToolkit/actions)
[![Tests](https://img.shields.io/badge/tests-integration%20%E2%9C%93-green)](tests)

## Why IdempotencyToolkit?

Duplicate API calls are a constant threat in distributed systems. Whether it's a double payment, a duplicate order, or a retried webhook — idempotency is essential.

Existing solutions like [IdempotentAPI](https://github.com/ikyriak/IdempotentAPI) are powerful, but they require **multiple NuGet packages, external locking libraries, and verbose configuration**.

**IdempotencyToolkit** gives you the same guarantees with **two packages, one line of configuration, and zero external dependencies** for distributed locking.

## Features

- 🛡️ **Guaranteed exactly‑once execution** – distributed locking prevents race conditions even under concurrent requests.
- 🧹 **Background cleanup** – expired records in PostgreSQL are automatically removed by a built‑in hosted service (configurable interval).
- 🔌 **Pluggable storage** – Redis, PostgreSQL, or In‑Memory (for development/testing).
- 🧘 **Minimal setup** – nstall a storage provider + ASP.NET Core package, add one line to `Program.cs`, apply an attribute.
- ⚙️ **Flexible configuration** – customizable key header, TTL, lock timeouts.
- 🧪 **Integration‑tested** – tested against real Redis and PostgreSQL containers via Testcontainers.
- 🏗️ **Production‑ready** – built on `NpgsqlDataSource`, `StackExchange.Redis`, and modern .NET practices.

## Quick Start
### 1. Install the required packages

IdempotencyToolkit is modular. You always need **a storage provider** and the **ASP.NET Core integration**.  
The core package is installed automatically as a dependency.

**For Redis:**
```bash
dotnet add package IdempotencyToolkit.Redis
dotnet add package IdempotencyToolkit.AspNetCore
```

**For PostgreSQL:**
```bash
dotnet add package IdempotencyToolkit.PostgreSql
dotnet add package IdempotencyToolkit.AspNetCore
```

> 💡 You never need to install `IdempotencyToolkit` directly.

### 2. Register in `Program.cs`

Make sure to add the required `using` directives at the top of your file:

```csharp
using Idempotency.Net.Extensions;           // for AddIdempotency
using Idempotency.Net.AspNetCore.Extensions; // for WithIdempotency
// Choose one of the following based on your storage:
using Idempotency.Net.Redis;                // for UseRedis
// using Idempotency.Net.PostgreSql;        // for UsePostgreSql
```

**Redis:**
```csharp
builder.Services.AddIdempotency()
                .UseRedis(options =>
                {
                    options.ConnectionString = "localhost:6379";
                });
```

**PostgreSQL:**
```csharp
builder.Services.AddIdempotency()
                .UsePostgreSql(options =>
                {
                    options.ConnectionString = "Host=localhost;...";
                });
```

**In‑Memory (for development/testing):**
```csharp
builder.Services.AddIdempotency()
                .UseInMemory();
```

### 3. Apply to controllers or Minimal API

**Controllers:**
```csharp
[ApiController, Route("[controller]")]
public class OrdersController : ControllerBase
{
    [HttpPost]
    [Idempotent]
    public IActionResult CreateOrder() => Ok(Guid.NewGuid());
}
```

**Minimal API:**
```csharp
app.MapPost("/orders", () => Results.Ok(Guid.NewGuid()))
   .WithIdempotency();
```

### 4. Send requests

Include the idempotency key header (default `X-Idempotency-Key`). The second identical request will return the cached response without executing the action again.

## How It Works (Race Condition Protection)

1. **Check cache** – if a response for the key exists and is not expired, return it immediately.
2. **Acquire distributed lock** – prevents other threads/instances from entering the same critical section.
3. **Double‑check cache** – another request might have finished while we waited for the lock.
4. **Execute business logic** – only if no cached response exists.
5. **Save response** – store the result with the configured TTL.
6. **Release lock** – allow other requests to use the cached result.

This guarantees that even simultaneous identical requests execute the business logic **exactly once**.

## Comparison with IdempotentAPI

| Feature                               | IdempotentAPI (v2.6)                                 | IdempotencyToolkit                                           |
|---------------------------------------|------------------------------------------------------|--------------------------------------------------------------|
| NuGet packages                        | 3–4 (core + cache + lock + provider)                 | 2                                                            |
| Lines of configuration                | 10–20                                                | 1–2                                                          |
| Distributed lock                      | Requires external library (RedLock.net or DistributedLock) | Built‑in (Redis `LockTake` / PostgreSQL `pg_advisory_xact_lock`) |
| PostgreSQL support (without Redis)    | Not available (lock requires Redis)                  | Yes – in‑table cache + advisory lock                         |
| In‑Memory provider                    | No                                                   | Yes                                                          |
| Controller support                    | Yes                                                  | Yes                                                          |
| Minimal API support                   | Yes                                                  | Yes                                                          |
| Cache‑only success responses          | Yes                                                  | Planned                                                      |

If you need advanced caching (FusionCache) or FastEndpoints, **IdempotentAPI** is a great choice.  
If you value simplicity, built‑in safety, and native PostgreSQL support, **IdempotencyToolkit** is for you.

## Configuration

### IdempotencyOptions

| Property    | Default               | Description                                    |
|-------------|-----------------------|------------------------------------------------|
| HeaderName  | `X-Idempotency-Key`   | Name of the idempotency key header.            |
| Expiration  | `24:00:00`            | How long to keep cached responses.             |
| LockExpiry  | `00:00:10`            | Maximum lifetime of a lock (prevents deadlocks).|

### Redis Options

| Property         | Default          | Description                  |
|------------------|------------------|------------------------------|
| ConnectionString | —                | Redis connection string.     |
| Database         | `0`              | Redis database index.        |
| KeyPrefix        | `idempotency:`   | Prefix for all Redis keys.   |

### PostgreSQL Options

| Property                 | Default                | Description                                                |
|--------------------------|------------------------|------------------------------------------------------------|
| ConnectionString         | —                      | PostgreSQL connection string.                              |
| Schema                   | `public`               | Database schema.                                           |
| TableName                | `idempotency_records`  | Name of the idempotency table.                             |
| EnableAutoCreateTable    | `true`                 | Automatically create the table if not exists.              |
| EnableBackgroundCleanup  | `true`                 | Enables automatic background deletion of expired records.  |
| CleanupInterval          | `00:05:00`             | How often the background cleanup runs.                     |


## Error Handling

If saving the idempotency record fails **after** the business logic has already been executed, an error is logged.  
A subsequent request with the same key may re-execute the operation.  
We recommend monitoring your logs for `Failed to save idempotency record` errors to detect storage issues early.

A future version will introduce an option to delay the response until the record is safely persisted.

## Known Limitations

- **PostgreSQL locking:** The PostgreSQL provider holds a database connection and transaction for the entire duration of the business logic execution. Under high load, this may exhaust the connection pool. For high‑throughput scenarios, consider load‑testing or using the Redis provider.
- **Response body size**: The library does not impose a limit on the cached response body size. It is the developer's responsibility to ensure that responses are not excessively large. A future version will include an optional `MaxResponseBodySize` setting.

## Running Tests

Integration tests require Docker. Run them from the solution root:

```bash
dotnet test tests/Idempotency.Net.Redis.IntegrationTests
dotnet test tests/Idempotency.Net.PostgreSql.IntegrationTests
dotnet test tests/Idempotency.Net.InMemory.IntegrationTests
dotnet test tests/Idempotency.Net.AspNetCore.IntegrationTests
```

## Contributing

Contributions are welcome! Feel free to open issues, suggest features, or submit pull requests.

## License

MIT
