using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Cantina.Api;

public sealed class GlobalConcurrencyMiddleware(RequestDelegate next, ConcurrencyLimiter limiter)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<DisableRateLimitingAttribute>() is not null)
        {
            await next(context);
            return;
        }
        using var lease = limiter.AttemptAcquire();
        if (!lease.IsAcquired)
        {
            context.Response.Headers.RetryAfter = "1";
            await ApiErrors.Problem(context, StatusCodes.Status429TooManyRequests).ExecuteAsync(context);
            return;
        }
        await next(context);
    }
}