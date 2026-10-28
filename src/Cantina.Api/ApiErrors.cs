using System.Diagnostics;
using System.Text.Json;
using Cantina.Application;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace Cantina.Api;

public sealed class ApiErrors(RequestDelegate next, ILogger<ApiErrors> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var supplied = context.Request.Headers["X-Correlation-ID"].ToString();
        context.TraceIdentifier = supplied.Length is > 0 and <= 64 &&
            supplied.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
            ? supplied : Guid.NewGuid().ToString("N");
        context.Response.Headers["X-Correlation-ID"] = context.TraceIdentifier;
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = context.TraceIdentifier });
        var start = Stopwatch.GetTimestamp();
        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            context.Abort();
        }
        catch (Exception exception) when (!context.Response.HasStarted)
        {
            context.Response.Clear();
            context.Response.Headers["X-Correlation-ID"] = context.TraceIdentifier;
            IResult result = exception switch
            {
                RequestValidationException validation => Results.ValidationProblem(validation.Errors,
                    extensions: new Dictionary<string, object?> { ["correlationId"] = context.TraceIdentifier }),
                BadHttpRequestException bad => Problem(context, bad.StatusCode),
                JsonException => Problem(context, 400),
                DbUpdateConcurrencyException => Problem(context, 409,
                    "The record changed since it was read. Re-read it and retry."),
                _ => Problem(context, 500)
            };
            if (exception is not (RequestValidationException or BadHttpRequestException or JsonException
                or DbUpdateConcurrencyException))
                logger.LogError("Request failed with exception type {ExceptionType}", exception.GetType().Name);
            await result.ExecuteAsync(context);
        }
        finally
        {
            var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "(unmatched)";
            logger.LogInformation("HTTP {Method} {Route} responded {StatusCode} in {ElapsedMs} ms",
                context.Request.Method, route, context.Response.StatusCode, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
    }

    public static IResult Problem(HttpContext context, int status, string? title = null) =>
        Results.Problem(statusCode: status, title: title ?? ReasonPhrases.GetReasonPhrase(status),
            extensions: new Dictionary<string, object?> { ["correlationId"] = context.TraceIdentifier });
}
