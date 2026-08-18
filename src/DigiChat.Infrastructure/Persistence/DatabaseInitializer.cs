using DigiChat.Domain.Entities;
using DigiChat.Infrastructure.Seeding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DigiChat.Infrastructure.Persistence;

/// <summary>
/// Startup bootstrap: applies pending EF migrations, seeds the lineage roster,
/// and guarantees the Generation #1 + AppState singleton rows exist. Safe to
/// run on every startup — every step is idempotent. Crucially, restarting the
/// backend never creates a new stream session (spec §7).
/// </summary>
public class DatabaseInitializer(LineageSeeder seeder, ILogger<DatabaseInitializer> logger)
{
    public async Task InitializeAsync(DigiChatDbContext db, string lineageFilePath, CancellationToken ct = default)
    {
        if (db.Database.IsSqlServer())
        {
            await DatabaseLocation.DropStaleRegistrationAsync(
                db.Database.GetConnectionString(), logger, ct);
            // CanConnectAsync swallows every failure and returns false, so it
            // cannot tell "this database does not exist yet" from "LocalDB was
            // still warming up and the first connect timed out". Treating the
            // second as the first would skip the pre-migration backup and then
            // migrate a populated database anyway — losing the safety
            // net precisely when it is needed. Retry before concluding absence.
            var databaseExists = await CanConnectWithRetryAsync(db, ct);
            if (databaseExists)
            {
                var pending = await db.Database.GetPendingMigrationsAsync(ct);
                if (pending.Any())
                    await DatabaseLocation.BackupBeforeMigrationAsync(
                        db.Database.GetConnectionString(), logger, ct);
            }
            logger.LogInformation("Applying database migrations…");
            await db.Database.MigrateAsync(ct);
        }
        else
        {
            // Test providers (SQLite) build the schema straight from the model;
            // migrations are SQL Server artifacts.
            await db.Database.EnsureCreatedAsync(ct);
        }

        await seeder.SeedAsync(db, lineageFilePath, ct);

        var state = await db.AppStates.FirstOrDefaultAsync(s => s.Id == AppState.SingletonId, ct);
        if (state is null)
        {
            var generation = await db.Generations
                .Where(g => g.UndoneUtc == null)
                .OrderByDescending(g => g.Number)
                .FirstOrDefaultAsync(ct);

            if (generation is null)
            {
                generation = new Generation { Number = 1, StartedUtc = DateTime.UtcNow };
                db.Generations.Add(generation);
            }

            db.AppStates.Add(new AppState
            {
                Id = AppState.SingletonId,
                CurrentGeneration = generation,
                CurrentStreamSessionId = null,
                CurrentStage = Domain.DigivolutionStage.Fresh,
                UpdatedUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Bootstrapped Generation 1 and initial application state");
        }

        // Prune idempotency ledger entries older than 24h (EventSub redelivery
        // happens within seconds; a day is generous).
        var cutoff = DateTime.UtcNow.AddDays(-1);
        var pruned = await db.ProcessedChatEvents.Where(p => p.ReceivedUtc < cutoff).ExecuteDeleteAsync(ct);
        if (pruned > 0)
            logger.LogInformation("Pruned {Count} old processed-event rows", pruned);
    }

    /// <summary>
    /// Whether the database can be reached, retrying briefly before answering
    /// "no". A cold LocalDB instance — first launch after a reboot — can exceed
    /// SqlClient's connect timeout on the very first attempt, and a false
    /// negative here silently skips the pre-migration backup.
    /// </summary>
    private async Task<bool> CanConnectWithRetryAsync(DigiChatDbContext db, CancellationToken ct)
    {
        const int attempts = 3;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            if (await db.Database.CanConnectAsync(ct)) return true;
            if (attempt == attempts) break;

            logger.LogInformation(
                "Database not reachable on attempt {Attempt}/{Attempts}; retrying before treating it as absent",
                attempt, attempts);
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }

        return false;
    }
}
