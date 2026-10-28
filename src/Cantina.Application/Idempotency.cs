namespace Cantina.Application;

/// <summary>A captured HTTP response that can be replayed verbatim for a repeated request.</summary>
public sealed record IdempotentResponse(int StatusCode, string? Location, string Body);

public abstract record IdempotencyState
{
    /// <summary>This caller now owns the key and must complete or release it.</summary>
    public sealed record Reserved : IdempotencyState;

    /// <summary>An earlier identical request already finished; replay its response.</summary>
    public sealed record Completed(IdempotentResponse Response) : IdempotencyState;

    /// <summary>An identical request is still running; the caller should retry shortly.</summary>
    public sealed record InProgress : IdempotencyState;

    /// <summary>The key was already used for a different request payload.</summary>
    public sealed record Conflict : IdempotencyState;
}

/// <summary>
/// Reservation protocol that lets a client safely retry a non-idempotent write: the first caller
/// reserves the key, and later callers either replay the stored response or are told to wait.
/// </summary>
public interface IIdempotencyStore
{
    Task<IdempotencyState> BeginAsync(string userId, string key, string fingerprint, CancellationToken ct);
    Task CompleteAsync(string userId, string key, IdempotentResponse response, CancellationToken ct);
    Task ReleaseAsync(string userId, string key, CancellationToken ct);
}
