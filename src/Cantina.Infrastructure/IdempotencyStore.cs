using Cantina.Application;
using Cantina.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cantina.Infrastructure;

public sealed class IdempotencyRecord
{
    public string UserId { get; set; } = "";
    public string Key { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public Guid Owner { get; set; }
    public int StatusCode { get; set; }
    public string? Location { get; set; }
    public string? Body { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
}

public sealed class IdempotencyStore(CantinaDbContext db, TimeProvider clock, CatalogueCache cache) : IIdempotencyStore
{
    // Long enough to outlive client retry windows, short enough to keep the table small.
    public static readonly TimeSpan Retention = TimeSpan.FromHours(24);
    public static readonly TimeSpan ReservationLifetime = TimeSpan.FromMinutes(2);

    public async Task<IdempotencyState> BeginAsync(string userId, string key, string fingerprint, CancellationToken ct)
    {
        var owner = Guid.NewGuid();
        var now = clock.GetUtcNow();
        await db.IdempotencyRecords.Where(record => record.UserId == userId && record.Key != key
            && record.CreatedAtUtc <= now - Retention).ExecuteDeleteAsync(ct);
        var storedNow = db.Database.IsSqlite() ? (object)now.UtcTicks : now;
        var storedRetentionLimit = db.Database.IsSqlite() ? (object)(now - Retention).UtcTicks : now - Retention;
        var storedReservationLimit = db.Database.IsSqlite()
            ? (object)(now - ReservationLifetime).UtcTicks
            : now - ReservationLifetime;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "IdempotencyRecords" ("UserId", "Key", "Fingerprint", "Owner", "StatusCode", "CreatedAtUtc")
            VALUES ({userId}, {key}, {fingerprint}, {owner}, 0, {storedNow})
            ON CONFLICT ("UserId", "Key") DO UPDATE
            SET "Fingerprint" = EXCLUDED."Fingerprint", "Owner" = EXCLUDED."Owner",
                "StatusCode" = 0, "Location" = NULL, "Body" = NULL,
                "CreatedAtUtc" = EXCLUDED."CreatedAtUtc", "CompletedAtUtc" = NULL
            WHERE "IdempotencyRecords"."CreatedAtUtc" <= {storedRetentionLimit}
               OR ("IdempotencyRecords"."CompletedAtUtc" IS NULL
                   AND "IdempotencyRecords"."CreatedAtUtc" <= {storedReservationLimit})
            """, ct);
        var existing = await Find(userId, key).AsNoTracking().SingleOrDefaultAsync(ct);
        if (existing?.Owner == owner) return new IdempotencyState.Reserved(owner);
        return Describe(existing, fingerprint);
    }

    public async Task<IdempotentResponse> ExecuteAsync(string userId, string key, Guid owner,
        Func<Task<IdempotentResponse>> write, CancellationToken ct)
    {
        var stores = new HashSet<Guid>();
        var result = await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var recordQuery = db.IdempotencyRecords.Where(record => record.UserId == userId && record.Key == key);
            if (!db.Database.IsSqlite())
                recordQuery = db.IdempotencyRecords.FromSqlInterpolated($"""
                    SELECT * FROM "IdempotencyRecords" WHERE "UserId" = {userId} AND "Key" = {key} FOR UPDATE
                    """);
            var record = await recordQuery.AsNoTracking().SingleOrDefaultAsync(ct);
            if (record is null || record.Owner != owner)
                throw new InvalidOperationException("The idempotency reservation is no longer owned by this request.");
            if (record.CompletedAtUtc is not null)
                return new IdempotentResponse(record.StatusCode, record.Location, record.Body ?? "");

            var response = await write();
            stores.UnionWith(db.ChangeTracker.Entries<MenuItem>().Select(entry => entry.Entity.StoreId));
            var completed = await Find(userId, key).Where(entry => entry.Owner == owner && entry.CompletedAtUtc == null)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(entry => entry.StatusCode, response.StatusCode)
                    .SetProperty(entry => entry.Location, response.Location)
                    .SetProperty(entry => entry.Body, response.Body)
                    .SetProperty(entry => entry.CompletedAtUtc, clock.GetUtcNow()), ct);
            if (completed != 1) throw new InvalidOperationException("The idempotency reservation could not be completed.");
            await transaction.CommitAsync(ct);
            return response;
        });
        foreach (var store in stores) cache.Invalidate(store);
        return result;
    }

    public Task ReleaseAsync(string userId, string key, Guid owner, CancellationToken ct) =>
        Find(userId, key).Where(x => x.Owner == owner && x.CompletedAtUtc == null).ExecuteDeleteAsync(ct);

    private IQueryable<IdempotencyRecord> Find(string userId, string key) =>
        db.IdempotencyRecords.Where(x => x.UserId == userId && x.Key == key);

    private static IdempotencyState Describe(IdempotencyRecord? record, string fingerprint) =>
        record is null ? new IdempotencyState.InProgress()
            : record.Fingerprint != fingerprint ? new IdempotencyState.Conflict()
            : record.CompletedAtUtc is null ? new IdempotencyState.InProgress()
            : new IdempotencyState.Completed(new IdempotentResponse(record.StatusCode, record.Location, record.Body ?? ""));
}
