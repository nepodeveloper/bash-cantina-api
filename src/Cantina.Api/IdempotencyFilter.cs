using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cantina.Application;

namespace Cantina.Api;

/// <summary>
/// Makes a non-idempotent write safely retryable. The first caller reserves the supplied
/// <c>Idempotency-Key</c>, the response is captured verbatim, and an identical retry replays it
/// instead of performing the work twice.
/// </summary>
public sealed class IdempotencyFilter<TRequest>(IIdempotencyStore store, ILogger<IdempotencyFilter<TRequest>> logger)
    : IEndpointFilter where TRequest : notnull
{
    private const string HeaderName = "Idempotency-Key";
    private const string ReplayHeaderName = "Idempotency-Replayed";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var supplied = http.Request.Headers[HeaderName].ToString();
        if (supplied.Length == 0) return await next(context);
        if (supplied.Length > 128 || !supplied.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':'))
            throw new RequestValidationException(new()
            {
                [HeaderName] = ["Supply at most 128 characters from A-Z, a-z, 0-9, hyphen, underscore, dot or colon."]
            });

        var userId = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return ApiErrors.Problem(http, 401);
        var fingerprint = Fingerprint(http, context);
        Guid owner;

        switch (await store.BeginAsync(userId, supplied, fingerprint, http.RequestAborted))
        {
            case IdempotencyState.Completed(var response):
                logger.LogInformation("Replaying idempotent response for key {IdempotencyKey}", supplied);
                http.Response.Headers[ReplayHeaderName] = "true";
                if (response.Location is { Length: > 0 } location) http.Response.Headers.Location = location;
                return Results.Content(response.Body, "application/json", Encoding.UTF8, response.StatusCode);
            case IdempotencyState.InProgress:
                http.Response.Headers.RetryAfter = "1";
                return ApiErrors.Problem(http, 409, "An identical request is still in progress. Retry shortly.");
            case IdempotencyState.Conflict:
                return ApiErrors.Problem(http, 422, "This Idempotency-Key was already used with a different request body.");
            case IdempotencyState.Reserved(var reservationOwner):
                owner = reservationOwner;
                break;
            default:
                throw new InvalidOperationException("Unknown idempotency state.");
        }

        IdempotentResponse captured;
        try
        {
            captured = await store.ExecuteAsync(userId, supplied, owner,
                () => CaptureAsync(http, context, next), http.RequestAborted);
        }
        catch
        {
            await store.ReleaseAsync(userId, supplied, owner, CancellationToken.None);
            throw;
        }
        if (captured.Location is { Length: > 0 } createdLocation) http.Response.Headers.Location = createdLocation;
        return Results.Content(captured.Body, "application/json", Encoding.UTF8, captured.StatusCode);
    }

    private static async Task<IdempotentResponse> CaptureAsync(HttpContext http,
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var originalBody = http.Response.Body;
        using var buffer = new MemoryStream();
        http.Response.Body = buffer;
        try
        {
            if (await next(context) is IResult result) await result.ExecuteAsync(http);
            var body = Encoding.UTF8.GetString(buffer.ToArray());
            return new IdempotentResponse(http.Response.StatusCode, http.Response.Headers.Location.ToString(), body);
        }
        finally
        {
            http.Response.Body = originalBody;
        }
    }

    private static string Fingerprint(HttpContext http, EndpointFilterInvocationContext context)
    {
        var request = context.Arguments.OfType<TRequest>().FirstOrDefault();
        var payload = $"{http.Request.Method}\n{http.Request.Path}\n{JsonSerializer.Serialize(request)}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }
}
