using Idempotency.Net.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace Idempotency.Net.AspNetCore.Extensions;

/// <summary>
/// Extension methods for adding idempotency to Minimal API endpoints.
/// </summary>
public static class RouteHandlerBuilderExtensions
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Adds idempotency support to a Minimal API endpoint.
    /// </summary>
    /// <param name="builder">The <see cref="RouteHandlerBuilder"/>.</param>
    /// <returns>The builder for chaining.</returns>
    public static RouteHandlerBuilder WithIdempotency(this RouteHandlerBuilder builder)
    {
        builder.AddEndpointFilter(async (context, next) =>
        {
            IServiceProvider requestServices = context.HttpContext.RequestServices;
            IdempotencyOptions options = requestServices.GetRequiredService<IOptions<IdempotencyOptions>>().Value;

            if (!TryGetIdempotencyKey(context.HttpContext, options, out var key))
                return await next(context).ConfigureAwait(false);

            if (key.Length > 256)
            {
                var loggerFactory = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>();
                var logger = loggerFactory.CreateLogger("Idempotency.Net.AspNetCore.MinimalApi");
                logger.LogWarning("Idempotency key exceeds maximum length of 256 characters");
                return Results.BadRequest("Idempotency key exceeds maximum length of 256 characters");
            }

            IdempotencyStore store = requestServices.GetRequiredService<IdempotencyStore>();
            CancellationToken cancellationToken = context.HttpContext.RequestAborted;

            IdempotencyRecord? cached = await store.GetAsync(key, cancellationToken).ConfigureAwait(false);
            if (cached is not null)
                return new CachedIdempotencyResult(cached);

            IIdempotencyLock lockProvider = requestServices.GetRequiredService<IIdempotencyLock>();
            bool lockAcquired = await lockProvider.AcquireAsync(key, cancellationToken).ConfigureAwait(false);
            if (!lockAcquired)
                return Results.StatusCode(423);

            try
            {
                cached = await store.GetAsync(key, cancellationToken).ConfigureAwait(false);
                if (cached is not null)
                    return new CachedIdempotencyResult(cached);

                object? result = await next(context).ConfigureAwait(false);

                IdempotencyRecord? resultToPersist = ToRecord(key, result, options);
                if (resultToPersist is not null)
                {
                    try
                    {
                        await store.SaveAsync(resultToPersist, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        var loggerFactory = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>();
                        var logger = loggerFactory.CreateLogger("Idempotency.Net.AspNetCore.MinimalApi");
                        logger.LogError(ex, "Failed to save idempotency record for key {Key}. Subsequent requests may re-execute the operation.", key);
                    }
                }

                return result;
            }
            finally
            {
                try
                {
                    await lockProvider.ReleaseAsync(key).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    var loggerFactory = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>();
                    var logger = loggerFactory.CreateLogger("Idempotency.Net.AspNetCore.MinimalApi");
                    logger.LogError(ex, "Failed to release idempotency lock for key {Key}", key);
                }
            }
        });

        return builder;
    }

    private static bool TryGetIdempotencyKey(HttpContext httpContext, IdempotencyOptions options, out string key)
    {
        key = string.Empty;

        if (!httpContext.Request.Headers.TryGetValue(options.HeaderName, out var values))
            return false;

        string value = values.ToString().Trim();
        if (string.IsNullOrWhiteSpace(value))
            return false;

        key = value;
        return true;
    }

    private static IdempotencyRecord? ToRecord(string key, object? result, IdempotencyOptions options)
    {
        DateTimeOffset createdAt = DateTimeOffset.UtcNow;
        DateTimeOffset expiresAt = createdAt.Add(options.Expiration);

        if (result is null)
        {
            return new IdempotencyRecord
            {
                Key = key,
                StatusCode = StatusCodes.Status200OK,
                CreatedAt = createdAt,
                ExpiresAt = expiresAt,
            };
        }

        if (result is string text)
        {
            return new IdempotencyRecord
            {
                Key = key,
                StatusCode = StatusCodes.Status200OK,
                ResponseBody = text,
                ContentType = "text/plain; charset=utf-8",
                CreatedAt = createdAt,
                ExpiresAt = expiresAt,
            };
        }

        if (result is IValueHttpResult valueResult)
        {
            int statusCode = result is IStatusCodeHttpResult statusResult && statusResult.StatusCode is not null
                ? statusResult.StatusCode.Value
                : StatusCodes.Status200OK;

            string? contentType = result is IContentTypeHttpResult contentTypeResult
                ? contentTypeResult.ContentType
                : "application/json; charset=utf-8";

            return new IdempotencyRecord
            {
                Key = key,
                StatusCode = statusCode,
                ResponseBody = JsonSerializer.Serialize(valueResult.Value, SerializerOptions),
                ContentType = contentType,
                CreatedAt = createdAt,
                ExpiresAt = expiresAt,
            };
        }

        if (result is IResult)
        {
            int statusCode = (result as IStatusCodeHttpResult)?.StatusCode ?? StatusCodes.Status200OK;
            return new IdempotencyRecord { Key = key, StatusCode = statusCode, CreatedAt = createdAt, ExpiresAt = expiresAt };
        }
        return null;
    }

    private sealed class CachedIdempotencyResult : IResult
    {
        private readonly IdempotencyRecord _cached;

        public CachedIdempotencyResult(IdempotencyRecord cached)
        {
            _cached = cached;
        }

        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = _cached.StatusCode;

            if (!string.IsNullOrWhiteSpace(_cached.ContentType))
                httpContext.Response.ContentType = _cached.ContentType;

            if (!string.IsNullOrEmpty(_cached.ResponseBody))
                await httpContext.Response.WriteAsync(_cached.ResponseBody, httpContext.RequestAborted).ConfigureAwait(false);
        }
    }
}