using System.Runtime.InteropServices;
using System.Text.Json;

using Idempotency.Net.Abstractions;
using Idempotency.Net.Services;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Diagnostics;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Idempotency.Net.AspNetCore;

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class IdempotentAttribute : Attribute, IAsyncActionFilter
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        IServiceProvider requestServices = context.HttpContext.RequestServices;
        IdempotencyOptions options = requestServices.GetRequiredService<IOptions<IdempotencyOptions>>().Value;

        if (!TryGetIdempotencyKey(context.HttpContext, options, out var key))
        {
            await next().ConfigureAwait(false);
            return;
        }

        IdempotencyService service = requestServices.GetRequiredService<IdempotencyService>();
        CancellationToken cancellationToken = context.HttpContext.RequestAborted;

        IdempotencyRecord? cached = await service.GetAsync(key, cancellationToken).ConfigureAwait(false);
        if (cached is not null)
        {
            context.Result = ToMvcResult(cached);
            return;
        }

        IIdempotencyLock lockProvider = requestServices.GetRequiredService<IIdempotencyLock>();
        bool lockAcquired = await lockProvider.AcquireAsync(key, cancellationToken).ConfigureAwait(false);
        if (!lockAcquired)
        {
            context.Result = new StatusCodeResult(423);
            return;
        }

        try
        {
            cached = await service.GetAsync(key, cancellationToken).ConfigureAwait(false);
            if (cached is not null)
            {
                context.Result = ToMvcResult(cached);
                return;
            }

            ActionExecutedContext executedContext = await next().ConfigureAwait(false);
            if (executedContext.Exception is not null && !executedContext.ExceptionHandled)
                return;

            IdempotencyRecord? resultToPersist = ToRecord(key, executedContext.Result, options);
            if (resultToPersist is not null)
                await service.SaveAsync(resultToPersist, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await lockProvider.ReleaseAsync(key).ConfigureAwait(false);
        }
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

    private static IActionResult ToMvcResult(IdempotencyRecord cached)
    {
        return cached.ResponseBody is null
            ? new StatusCodeResult(cached.StatusCode)
            : new ContentResult
            {
                StatusCode = cached.StatusCode,
                ContentType = cached.ContentType,
                Content = cached.ResponseBody,
            };
    }

    private static IdempotencyRecord? ToRecord(string key, IActionResult? result, IdempotencyOptions options)
    {
        if (result is FileResult)
            return null;

        DateTimeOffset createdAt = DateTimeOffset.UtcNow;
        DateTimeOffset expiresAt = createdAt.Add(options.Expiration);

        return result switch
        {
            ContentResult contentResult => new IdempotencyRecord
            {
                Key = key,
                StatusCode = contentResult.StatusCode ?? StatusCodes.Status200OK,
                ResponseBody = contentResult.Content,
                ContentType = contentResult.ContentType,
                CreatedAt = createdAt,
                ExpiresAt = expiresAt,
            },
            JsonResult jsonResult => new IdempotencyRecord
            {
                Key = key,
                StatusCode = jsonResult.StatusCode ?? StatusCodes.Status200OK,
                ResponseBody = JsonSerializer.Serialize(jsonResult.Value, SerializerOptions),
                ContentType = "application/json; charset=utf-8",
                CreatedAt = createdAt,
                ExpiresAt = expiresAt,
            },
            ObjectResult objectResult => new IdempotencyRecord
            {
                Key = key,
                StatusCode = objectResult.StatusCode ?? StatusCodes.Status200OK,
                ResponseBody = JsonSerializer.Serialize(objectResult.Value, SerializerOptions),
                ContentType = objectResult.ContentTypes.Count > 0 ? objectResult.ContentTypes[0] : "application/json; charset=utf-8",
                CreatedAt = createdAt,
                ExpiresAt = expiresAt,
            },
            StatusCodeResult statusCodeResult => new IdempotencyRecord
            {
                Key = key,
                StatusCode = statusCodeResult.StatusCode,
                CreatedAt = createdAt,
                ExpiresAt = expiresAt,
            },
            EmptyResult => new IdempotencyRecord
            {
                Key = key,
                StatusCode = StatusCodes.Status200OK,
                CreatedAt = createdAt,
                ExpiresAt = expiresAt,
            },
            _ => null,
        };
    }
}