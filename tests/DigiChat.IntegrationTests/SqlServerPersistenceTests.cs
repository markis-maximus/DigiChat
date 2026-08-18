using DigiChat.Domain.Entities;
using DigiChat.Domain.Tests;
using DigiChat.Infrastructure.Persistence;
using DigiChat.Infrastructure.Seeding;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DigiChat.IntegrationTests;

/// <summary>
/// Real SQL Server (LocalDB) tests: migrations, seed, unique constraints, and
/// persistence across a simulated restart. Skipped automatically when LocalDB
/// is not installed. Requires generated EF migrations (dotnet ef migrations add).
/// </summary>
public sealed class LocalDbFactAttribute : FactAttribute
{
    private static readonly bool Available = Probe();

    public LocalDbFactAttribute()
    {
        if (!Available) Skip = "SQL Server LocalDB is not available on this machine.";
    }

    private static bool Probe()
    {
        try
        {
            using var conn = new SqlConnection(@"Server=(localdb)\MSSQLLocalDB;Database=master;Trusted_Connection=True;Connect Timeout=5");
            conn.Open();
            return true;
        }
        catch
        {
            return false;
        }
    }
}

public class SqlServerPersistenceTests
{
    // Throwaway databases, but still kept in the repo's data\db folder rather
    // than the user profile (see DatabaseLocation); each test drops its own.
    private static string ConnectionFor(string dbName, string? fileStem = null) =>
        DatabaseLocation.Resolve(
            $@"Server=(localdb)\MSSQLLocalDB;AttachDbFilename=%DBDIR%\{fileStem ?? dbName}.mdf;Initial Catalog={dbName};Trusted_Connection=True;MultipleActiveResultSets=true");

    private static DbContextOptions<DigiChatDbContext> OptionsFor(string dbName, string? fileStem = null) =>
        new DbContextOptionsBuilder<DigiChatDbContext>()
            .UseSqlServer(ConnectionFor(dbName, fileStem))
            .Options;

    [LocalDbFact]
    public async Task Migrate_Seed_Restart_PreservesEverything()
    {
        var dbName = $"DigiChatTest_{Guid.NewGuid():N}";
        var options = OptionsFor(dbName);
        var seeder = new LineageSeeder(NullLogger<LineageSeeder>.Instance);
        var initializer = new DatabaseInitializer(seeder, NullLogger<DatabaseInitializer>.Instance);
        var roster = TestHarness.FindRosterFile();

        try
        {
            // First "boot".
            await using (var db = new DigiChatDbContext(options))
            {
                await initializer.InitializeAsync(db, roster);
                Assert.Equal(30, await db.Lineages.CountAsync());
                Assert.Equal(150, await db.DigimonForms.CountAsync());
            }

            // Second "boot" — idempotent, nothing duplicated, state intact.
            await using (var db = new DigiChatDbContext(options))
            {
                await initializer.InitializeAsync(db, roster);
                Assert.Equal(30, await db.Lineages.CountAsync());
                Assert.Equal(150, await db.DigimonForms.CountAsync());
                Assert.Equal(1, await db.Generations.CountAsync());
                var state = await db.AppStates.SingleAsync();
                Assert.Null(state.CurrentStreamSessionId); // restarts never create sessions
            }
        }
        finally
        {
            await using var db = new DigiChatDbContext(options);
            await db.Database.EnsureDeletedAsync();
        }
    }

    [LocalDbFact]
    public async Task UniqueConstraints_PreventDuplicateAssignmentAtDatabaseLevel()
    {
        var dbName = $"DigiChatTest_{Guid.NewGuid():N}";
        var options = OptionsFor(dbName);
        var seeder = new LineageSeeder(NullLogger<LineageSeeder>.Instance);
        var initializer = new DatabaseInitializer(seeder, NullLogger<DatabaseInitializer>.Instance);

        try
        {
            await using var db = new DigiChatDbContext(options);
            await initializer.InitializeAsync(db, TestHarness.FindRosterFile());

            var gen = await db.Generations.SingleAsync();
            var lineage = await db.Lineages.FirstAsync();
            var v1 = new Viewer
            { TwitchUserId = "1", Login = "a", DisplayName = "A", FirstSeenUtc = DateTime.UtcNow, LastSeenUtc = DateTime.UtcNow };
            var v2 = new Viewer
            { TwitchUserId = "2", Login = "b", DisplayName = "B", FirstSeenUtc = DateTime.UtcNow, LastSeenUtc = DateTime.UtcNow };
            db.AddRange(v1, v2);
            db.Assignments.Add(new() { Viewer = v1, GenerationId = gen.Id, LineageId = lineage.Id, AssignedUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();

            // Same lineage, same generation, different viewer → must be rejected.
            db.Assignments.Add(new() { Viewer = v2, GenerationId = gen.Id, LineageId = lineage.Id, AssignedUtc = DateTime.UtcNow });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        finally
        {
            await using var db = new DigiChatDbContext(options);
            await db.Database.EnsureDeletedAsync();
        }
    }

    [LocalDbFact]
    public async Task MissingFileInSecondCheckout_NeverDropsRegisteredDatabaseFromFirstCheckout()
    {
        var catalog = $"DigiChatCollision_{Guid.NewGuid():N}";
        var firstStem = $"{catalog}_First";
        var secondStem = $"{catalog}_Second";
        var firstOptions = OptionsFor(catalog, firstStem);
        var secondConnection = ConnectionFor(catalog, secondStem);
        var firstFile = new SqlConnectionStringBuilder(ConnectionFor(catalog, firstStem)).AttachDBFilename;
        var secondFile = new SqlConnectionStringBuilder(secondConnection).AttachDBFilename;

        try
        {
            var seeder = new LineageSeeder(NullLogger<LineageSeeder>.Instance);
            var initializer = new DatabaseInitializer(seeder, NullLogger<DatabaseInitializer>.Instance);
            await using (var db = new DigiChatDbContext(firstOptions))
                await initializer.InitializeAsync(db, TestHarness.FindRosterFile());

            Assert.True(File.Exists(firstFile));
            Assert.False(File.Exists(secondFile));

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                DatabaseLocation.DropStaleRegistrationAsync(
                    secondConnection, NullLogger.Instance));

            Assert.Contains("another checkout", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(firstFile));

            await using var verify = new DigiChatDbContext(firstOptions);
            Assert.Equal(30, await verify.Lineages.CountAsync());
        }
        finally
        {
            await using var cleanup = new DigiChatDbContext(firstOptions);
            await cleanup.Database.EnsureDeletedAsync();
        }
    }

    [LocalDbFact]
    public async Task UpgradeFromPreHeldSchema_BackfillsParticipantAdmittedDuringDeath()
    {
        var dbName = $"DigiChatUpgrade_{Guid.NewGuid():N}";
        var options = OptionsFor(dbName);
        var diedUtc = DateTime.UtcNow.AddMinutes(-2);
        var joinedUtc = diedUtc.AddMinutes(1);

        try
        {
            await using (var old = new DigiChatDbContext(options))
            {
                await old.GetService<IMigrator>()
                    .MigrateAsync("20260816050552_AddGenerationDiedUtc");
                await old.Database.ExecuteSqlInterpolatedAsync($"""
                    DECLARE @generationId int;
                    DECLARE @sessionId int;
                    DECLARE @viewerId int;

                    INSERT INTO Generations (Number, StartedUtc, EndedUtc, UndoneUtc, DiedUtc)
                    VALUES (1, {diedUtc.AddHours(-1)}, NULL, NULL, {diedUtc});
                    SET @generationId = SCOPE_IDENTITY();

                    INSERT INTO StreamSessions (Number, StartedUtc, EndedUtc)
                    VALUES (1, {diedUtc.AddHours(-1)}, NULL);
                    SET @sessionId = SCOPE_IDENTITY();

                    INSERT INTO Viewers (TwitchUserId, Login, DisplayName, FirstSeenUtc, LastSeenUtc)
                    VALUES ('upgrade-user', 'upgrade-user', 'Upgrade User', {joinedUtc}, {joinedUtc});
                    SET @viewerId = SCOPE_IDENTITY();

                    INSERT INTO Participants (StreamSessionId, ViewerId, JoinedUtc)
                    VALUES (@sessionId, @viewerId, {joinedUtc});

                    INSERT INTO Assignments (ViewerId, GenerationId, LineageId, AssignedUtc)
                    VALUES (@viewerId, @generationId, NULL, {joinedUtc});

                    INSERT INTO AppStates
                        (Id, CurrentGenerationId, CurrentStreamSessionId, CurrentStage, UpdatedUtc)
                    VALUES (1, @generationId, @sessionId, 0, {joinedUtc});
                    """);
            }

            await using (var upgraded = new DigiChatDbContext(options))
            {
                await upgraded.GetService<IMigrator>().MigrateAsync();
                var participant = await upgraded.Participants.SingleAsync();
                Assert.True(participant.HeldForReincarnation);
            }
        }
        finally
        {
            await using var cleanup = new DigiChatDbContext(options);
            await cleanup.Database.EnsureDeletedAsync();
        }
    }

    [LocalDbFact]
    public async Task SqlBackup_CreatesNonemptyVerifiedRecoveryFile()
    {
        var dbName = $"DigiChatBackup_{Guid.NewGuid():N}";
        var connectionString = ConnectionFor(dbName);
        var options = OptionsFor(dbName);
        string? backupPath = null;

        try
        {
            var initializer = new DatabaseInitializer(
                new LineageSeeder(NullLogger<LineageSeeder>.Instance),
                NullLogger<DatabaseInitializer>.Instance);
            await using (var db = new DigiChatDbContext(options))
                await initializer.InitializeAsync(db, TestHarness.FindRosterFile());

            backupPath = await DatabaseLocation.BackupBeforeMigrationAsync(
                connectionString, NullLogger.Instance);

            var backup = new FileInfo(backupPath);
            Assert.True(backup.Exists);
            Assert.True(backup.Length > 0);
        }
        finally
        {
            await using var cleanup = new DigiChatDbContext(options);
            await cleanup.Database.EnsureDeletedAsync();
            if (backupPath is not null && File.Exists(backupPath))
                File.Delete(backupPath);
        }
    }
}
