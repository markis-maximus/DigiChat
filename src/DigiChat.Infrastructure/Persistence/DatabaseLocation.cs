using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace DigiChat.Infrastructure.Persistence;

/// <summary>
/// Keeps the database file with the rest of the project instead of the Windows
/// user profile. A connection string may use the <c>%DBDIR%</c> token inside
/// AttachDbFilename; it is replaced with &lt;repo root&gt;\data\db (or a data\db
/// folder next to the executable for a published build).
///
/// Without an explicit path LocalDB drops DigiChat.mdf in C:\Users\&lt;you&gt;, which
/// also means a stale file there makes every later CREATE DATABASE fail with
/// "Cannot create file … because it already exists" (SQL error 5170).
/// </summary>
public static class DatabaseLocation
{
    public const string DirectoryToken = "%DBDIR%";

    /// <summary>Replaces <see cref="DirectoryToken"/> and makes sure the folder exists.</summary>
    public static string Resolve(string connectionString, string? startDirectory = null)
    {
        if (!connectionString.Contains(DirectoryToken, StringComparison.OrdinalIgnoreCase))
            return connectionString;

        var directory = GetDatabaseDirectory(startDirectory);
        Directory.CreateDirectory(directory);
        return connectionString.Replace(
            DirectoryToken, directory.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Deleting the .mdf by hand — the documented way to start fresh — leaves the
    /// database still registered in the LocalDB instance. The next start then
    /// fails twice over: the missing file makes EF conclude the database does not
    /// exist, and its CREATE DATABASE hits SQL error 1801, "already exists".
    /// Clear that stale registration first. LocalDB catalog names are shared by
    /// every checkout owned by the same Windows user, so the registration must
    /// also point at this exact missing file. A catalog attached from another
    /// checkout is a hard stop: dropping it could delete that checkout's data.
    /// </summary>
    public static async Task DropStaleRegistrationAsync(
        string? connectionString, ILogger logger, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        SqlConnectionStringBuilder builder;
        try { builder = new SqlConnectionStringBuilder(connectionString); }
        catch (ArgumentException) { return; }

        var file = builder.AttachDBFilename;
        var catalog = builder.InitialCatalog;
        if (string.IsNullOrEmpty(file) || string.IsNullOrEmpty(catalog)) return;
        var expectedFile = Path.GetFullPath(file);
        if (FileExistsOrThrow(expectedFile, catalog)) return; // the normal case

        var master = new SqlConnectionStringBuilder(connectionString)
        {
            AttachDBFilename = "",
            InitialCatalog = "master",
        };

        await using var connection = new SqlConnection(master.ConnectionString);
        await connection.OpenAsync(ct);

        var registration = await GetRegistrationAsync(connection, catalog, ct);
        if (registration is null) return;

        if (registration.PrimaryDataFile is null ||
            !PathsEqual(registration.PrimaryDataFile, expectedFile))
        {
            var registeredAt = registration.PrimaryDataFile ?? "(unknown path)";
            throw new InvalidOperationException(
                $"LocalDB catalog '{catalog}' is already registered to '{registeredAt}', " +
                $"not this checkout's expected file '{expectedFile}'. DigiChat will not drop " +
                "or detach a database from another checkout. Stop the other copy or follow " +
                "docs/runbooks/database-recovery.md for a verified, targeted recovery.");
        }

        var filesStillPresent = registration.PhysicalFiles
            .Where(path => FileExistsOrThrow(path, catalog))
            .ToList();
        if (filesStillPresent.Count > 0)
        {
            throw new InvalidOperationException(
                $"LocalDB catalog '{catalog}' is registered to this checkout, but at least one " +
                $"database file still exists: {string.Join(", ", filesStillPresent)}. DigiChat " +
                "will not drop a registration while any data or log file remains. Back up the " +
                "files and follow docs/runbooks/database-recovery.md.");
        }

        logger.LogWarning(
            "Database {Catalog} is still registered but {File} is gone — dropping the stale "
            + "registration so a fresh database can be created.", catalog, file);

        // DROP reports a file-activation error for a database whose files are
        // missing, yet still removes the registration, which is all we need.
        SqlException? dropError = null;
        try
        {
            await using var drop = connection.CreateCommand();
            drop.CommandText = $"DROP DATABASE [{catalog.Replace("]", "]]")}]";
            await drop.ExecuteNonQueryAsync(ct);
        }
        catch (SqlException ex) { dropError = ex; /* expected; verified below */ }

        if (await GetRegistrationAsync(connection, catalog, ct) is not null)
            throw new InvalidOperationException(
                $"Could not clear the verified stale LocalDB registration for '{catalog}'. " +
                "No further database action was taken. Follow " +
                "docs/runbooks/database-recovery.md for targeted recovery.", dropError);
    }

    private sealed record Registration(string? PrimaryDataFile, IReadOnlyList<string> PhysicalFiles);

    private static async Task<Registration?> GetRegistrationAsync(
        SqlConnection connection, string catalog, CancellationToken ct)
    {
        await using var check = connection.CreateCommand();
        check.CommandText = """
            SELECT mf.type, mf.physical_name
            FROM sys.databases AS d
            LEFT JOIN sys.master_files AS mf ON mf.database_id = d.database_id
            WHERE d.name = @name
            ORDER BY mf.file_id
            """;
        check.Parameters.AddWithValue("@name", catalog);
        await using var reader = await check.ExecuteReaderAsync(ct);

        var found = false;
        string? primaryDataFile = null;
        var physicalFiles = new List<string>();
        while (await reader.ReadAsync(ct))
        {
            found = true;
            if (reader.IsDBNull(1)) continue;
            var physicalName = reader.GetString(1);
            physicalFiles.Add(physicalName);
            if (reader.GetByte(0) == 0 && primaryDataFile is null)
                primaryDataFile = physicalName;
        }
        return found ? new Registration(primaryDataFile, physicalFiles) : null;
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool FileExistsOrThrow(string path, string catalog)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // File.Exists collapses access failures into `false`. That is not a
            // safe basis for DROP DATABASE: an inaccessible file may still hold
            // live data and become deletable again later.
            throw new InvalidOperationException(
                $"Cannot verify whether database file '{path}' for catalog '{catalog}' exists. " +
                "DigiChat will not alter the registration until the path can be inspected.", ex);
        }
    }

    /// <summary>
    /// Creates a verified SQL Server backup beside the attached MDF before a
    /// schema migration. The caller first confirms that the database exists and
    /// has pending migrations; a backup failure deliberately blocks migration.
    /// </summary>
    public static async Task<string> BackupBeforeMigrationAsync(
        string? connectionString, ILogger logger, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("Cannot back up a database without a connection string.");

        var builder = new SqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(builder.AttachDBFilename)
            || string.IsNullOrWhiteSpace(builder.InitialCatalog))
            throw new InvalidOperationException(
                "Pre-migration backup requires AttachDbFilename and Initial Catalog.");

        var databaseFile = Path.GetFullPath(builder.AttachDBFilename);
        var backupDirectory = Path.Combine(
            Path.GetDirectoryName(databaseFile)
                ?? throw new InvalidOperationException("Database file has no parent directory."),
            "backups");
        Directory.CreateDirectory(backupDirectory);
        var safeCatalog = string.Concat(builder.InitialCatalog.Select(c =>
            Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var backupPath = Path.Combine(
            backupDirectory,
            $"{safeCatalog}-pre-migration-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}.bak");

        var master = new SqlConnectionStringBuilder(connectionString)
        {
            AttachDBFilename = "",
            InitialCatalog = "master",
        };
        await using var connection = new SqlConnection(master.ConnectionString);
        await connection.OpenAsync(ct);
        await using var backup = connection.CreateCommand();
        backup.CommandTimeout = 120;
        backup.CommandText =
            $"BACKUP DATABASE [{builder.InitialCatalog.Replace("]", "]]")}] " +
            "TO DISK = @path WITH COPY_ONLY, INIT, CHECKSUM";
        backup.Parameters.AddWithValue("@path", backupPath);
        await backup.ExecuteNonQueryAsync(ct);

        await using var verify = connection.CreateCommand();
        verify.CommandTimeout = 120;
        verify.CommandText = "RESTORE VERIFYONLY FROM DISK = @path WITH CHECKSUM";
        verify.Parameters.AddWithValue("@path", backupPath);
        await verify.ExecuteNonQueryAsync(ct);

        var info = new FileInfo(backupPath);
        if (!info.Exists || info.Length == 0)
            throw new IOException($"SQL Server reported a backup but '{backupPath}' is missing or empty.");
        logger.LogWarning("Created and verified pre-migration database backup at {BackupPath}", backupPath);
        return backupPath;
    }

    /// <summary>
    /// Walks up from the running assembly (bin\...\net10.0-windows) to the folder
    /// holding DigiChat.sln; falls back to the executable's own folder.
    /// </summary>
    public static string GetDatabaseDirectory(string? startDirectory = null)
    {
        for (var dir = new DirectoryInfo(startDirectory ?? AppContext.BaseDirectory);
             dir is not null;
             dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DigiChat.sln")))
                return Path.Combine(dir.FullName, "data", "db");
        }

        return Path.Combine(AppContext.BaseDirectory, "data", "db");
    }
}
