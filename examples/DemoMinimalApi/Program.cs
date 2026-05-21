using Idempotency.Net.AspNetCore.Extensions;
using Idempotency.Net.Extensions;
using Idempotency.Net.PostgreSql;
using Idempotency.Net.Redis;

var builder = WebApplication.CreateBuilder(args);

var idempotencyBuilder = builder.Services.AddIdempotency(options =>
{
    options.HeaderName = "X-Idempotency-Key";
    options.Expiration = TimeSpan.FromMinutes(30);
});

var provider = builder.Configuration["Idempotency:Provider"] ?? "Redis";

if (provider.Equals("Redis", StringComparison.OrdinalIgnoreCase))
{
    idempotencyBuilder.UseRedis(options =>
    {
        options.ConnectionString = builder.Configuration.GetConnectionString("Redis");
        options.Configuration = "localhost:6379,allowAdmin=true,ssl=false";
        options.InstanceName = "idempotency-example";
        options.Database = 0;
        options.KeyPrefix = "orders:";
        options.ConnectTimeout = TimeSpan.FromSeconds(3);
        options.SyncTimeout = TimeSpan.FromSeconds(2);
        options.AbortOnConnectFail = false;
    });
}
else if (provider.Equals("PostgreSql", StringComparison.OrdinalIgnoreCase))
{
    idempotencyBuilder.UsePostgreSql(options =>
    {
        options.ConnectionString = builder.Configuration.GetConnectionString("PostgreSql");
        options.Schema = "idempotency";
        options.TableName = "requests";
        options.EnableAutoCreateTable = true;
        options.CommandTimeout = TimeSpan.FromSeconds(15);
        options.CleanupBatchSize = 2_000;
    });
}
else
{
    throw new InvalidOperationException("Idempotency:Provider must be either 'Redis' or 'PostgreSql'.");
}

var app = builder.Build();

app.MapPost("/orders", (CreateOrderRequest request) =>
{
    var order = new
    {
        Id = Guid.NewGuid(),
        request.ProductId,
        request.Quantity,
        CreatedAt = DateTimeOffset.UtcNow,
        Provider = provider,
    };

    return Results.Ok(order);
})
.WithIdempotency();

app.Run();

public sealed record CreateOrderRequest(string ProductId, int Quantity);