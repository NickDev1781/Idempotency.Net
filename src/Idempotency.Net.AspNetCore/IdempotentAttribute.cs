using Idempotency.Net.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Diagnostics;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Idempotency.Net.AspNetCore;

/// <summary>
/// ASP.NET Core action filter that makes a controller action idempotent by checking and storing idempotency keys.
/// Requires an <see cref="IIdempotencyLock"/> and an <see cref="IdempotencyStore"/> to be registered in DI.
/// </summary>
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

        if (key.Length > 256)
        {
            var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<IdempotentAttribute>>();
            logger.LogWarning("Idempotency key exceeds maximum length of 256 characters");
            context.Result = new BadRequestObjectResult("Idempotency key exceeds maximum length of 256 characters");
            return;
        }

        IdempotencyStore store = requestServices.GetRequiredService<IdempotencyStore>();
        CancellationToken cancellationToken = context.HttpContext.RequestAborted;

        IdempotencyRecord? cached = await store.GetAsync(key, cancellationToken).ConfigureAwait(false);
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
            cached = await store.GetAsync(key, cancellationToken).ConfigureAwait(false);
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
            {
                try
                {
                    await store.SaveAsync(resultToPersist, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<IdempotentAttribute>>();
                    logger.LogError(ex, "Failed to save idempotency record for key {Key}. Subsequent requests may re-execute the operation.", key);
                }
            }

            if (resultToPersist is null && executedContext.Result is not FileResult)
            {
                var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<IdempotentAttribute>>();
                logger.LogWarning("Idempotent record not created for action result of type {ResultType}.", executedContext.Result?.GetType());
            }
        }
        finally
        {
            try
            {
                await lockProvider.ReleaseAsync(key).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<IdempotentAttribute>>();
                logger.LogError(ex, "Failed to release idempotency lock for key {Key}", key);
            }
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
                ResponseBody = jsonResult.Value is string s ? s : JsonSerializer.Serialize(jsonResult.Value, SerializerOptions),
                ContentType = "application/json; charset=utf-8",
                CreatedAt = createdAt,
                ExpiresAt = expiresAt,
            },
            ObjectResult objectResult => new IdempotencyRecord
            {
                Key = key,
                StatusCode = objectResult.StatusCode ?? StatusCodes.Status200OK,
                ResponseBody = objectResult.Value is string s ? s : JsonSerializer.Serialize(objectResult.Value, SerializerOptions),
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