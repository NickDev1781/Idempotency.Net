using System.Text.Json;

using Idempotency.Net.Abstractions;
using Idempotency.Net.Services;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Idempotency.Net.AspNetCore.Extensions;

public static class RouteHandlerBuilderExtensions
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static RouteHandlerBuilder WithIdempotency(this RouteHandlerBuilder builder)
    {
        builder.AddEndpointFilter(async (context, next) =>
        {
            IServiceProvider requestServices = context.HttpContext.RequestServices;
            IdempotencyOptions options = requestServices.GetRequiredService<IOptions<IdempotencyOptions>>().Value;

            if (!TryGetIdempotencyKey(context.HttpContext, options, out var key))
                return await next(context).ConfigureAwait(false);

            IdempotencyService service = requestServices.GetRequiredService<IdempotencyService>();
            CancellationToken cancellationToken = context.HttpContext.RequestAborted;

            IdempotencyRecord? cached = await service.GetAsync(key, cancellationToken).ConfigureAwait(false);
            if (cached is not null)
                return new CachedIdempotencyResult(cached);

            IIdempotencyLock lockProvider = requestServices.GetRequiredService<IIdempotencyLock>();
            bool lockAcquired = await lockProvider.AcquireAsync(key, cancellationToken).ConfigureAwait(false);
            if (!lockAcquired)
                return Results.StatusCode(423);

            try
            {
                cached = await service.GetAsync(key, cancellationToken).ConfigureAwait(false);
                if (cached is not null)
                    return new CachedIdempotencyResult(cached);

                object? result = await next(context).ConfigureAwait(false);

                IdempotencyRecord? resultToPersist = ToRecord(key, result, options);
                if (resultToPersist is not null)
                    await service.SaveAsync(resultToPersist, cancellationToken).ConfigureAwait(false);

                return result;
            }
            finally
            {
                await lockProvider.ReleaseAsync(key).ConfigureAwait(false);
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

        return result is IResult
            ? null
            : new IdempotencyRecord
            {
                Key = key,
                StatusCode = StatusCodes.Status200OK,
                ResponseBody = JsonSerializer.Serialize(result, SerializerOptions),
                ContentType = "application/json; charset=utf-8",
                CreatedAt = createdAt,
                ExpiresAt = expiresAt,
            };
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