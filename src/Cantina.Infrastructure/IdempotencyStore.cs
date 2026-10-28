using Cantina.Application;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Cantina.Infrastructure;

public sealed class IdempotencyRecord
{
    public string UserId { get; set; } = "";
    public string Key { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public int StatusCode { get; set; }
    public string? Location { get; set; }
    public string? Body { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
}

public sealed class IdempotencyStore(CantinaDbContext db, TimeProvider clock) : IIdempotencyStore
{
    // Long enough to outlive client retry windows, short enough to keep the table small.
    public static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    public async Task<IdempotencyState> BeginAsync(string userId, string key, string fingerprint, CancellationToken ct)
    {
        var existing = await Find(userId, key).AsNoTracking().SingleOrDefaultAsync(ct);
        if (existing is null)
        {
            try
            {
                // The primary key is the lock: only one concurrent caller can reserve a key.
                db.IdempotencyRecords.Add(new IdempotencyRecord
                {
                    UserId = userId, Key = key, Fingerprint = fingerprint, CreatedAtUtc = clock.GetUtcNow()
                });
                await db.SaveChangesAsync(ct);
                return new IdempotencyState.Reserved();
            }
            catch (DbUpdateException exception) when (exception.InnerException is PostgresException
                { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                db.ChangeTracker.Clear();
                existing = await Find(userId, key).AsNoTracking().SingleOrDefaultAsync(ct);
            }
        }
        return Describe(existing, fingerprint);
    }

    public async Task CompleteAsync(string userId, string key, IdempotentResponse response, CancellationToken ct)
    {
        await Find(userId, key).ExecuteUpdateAsync(setters => setters
            .SetProperty(x => x.StatusCode, response.StatusCode)
            .SetProperty(x => x.Location, response.Location)
            .SetProperty(x => x.Body, response.Body)
            .SetProperty(x => x.CompletedAtUtc, clock.GetUtcNow()), ct);
        // Bounded, index-seekable cleanup: this caller's own expired keys only, never a full-table scan.
        await db.IdempotencyRecords
            .Where(x => x.UserId == userId && x.CreatedAtUtc < clock.GetUtcNow() - Retention)
            .ExecuteDeleteAsync(ct);
    }

    public Task ReleaseAsync(string userId, string key, CancellationToken ct) =>
        Find(userId, key).Where(x => x.CompletedAtUtc == null).ExecuteDeleteAsync(ct);

    private IQueryable<IdempotencyRecord> Find(string userId, string key) =>
        db.IdempotencyRecords.Where(x => x.UserId == userId && x.Key == key);

    private static IdempotencyState Describe(IdempotencyRecord? record, string fingerprint) =>
        record is null ? new IdempotencyState.InProgress()
            : record.Fingerprint != fingerprint ? new IdempotencyState.Conflict()
            : record.CompletedAtUtc is null ? new IdempotencyState.InProgress()
            : new IdempotencyState.Completed(new IdempotentResponse(record.StatusCode, record.Location, record.Body ?? ""));
}
