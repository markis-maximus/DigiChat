# Database backup and recovery runbook

DigiChat attaches SQL Server LocalDB files in place:

| Mode | Catalog | Files |
|---|---|---|
| Live | `DigiChat` | `data/db/DigiChat.mdf` and its log file |
| Mock | `DigiChat_Mock` | `data/db/DigiChat.Mock.mdf` and its log file |

The live files hold real viewer and transition history. Never delete, replace,
detach, or drop the live database without explicit human approval and a
verified backup. Mock data is disposable, but target it just as precisely.

## First response to any database error

1. Stop the DigiChat process you started.
2. Confirm no other DigiChat process owns port 5170.
3. Read the complete SQL error and its file/catalog name.
4. Do not delete a file merely because startup called it stale.

Two pieces of state can drift apart: the `.mdf` on disk and its registration in
the `MSSQLLocalDB` instance.

| Symptom | Likely state | Safe direction |
|---|---|---|
| SQL 5170, “cannot create file … already exists” | File exists, catalog registration does not | Preserve the file; inspect and attach it unless a human confirms it is disposable |
| SQL 1801, “database already exists” | Catalog remains, expected file is gone | Startup clears it only when the catalog points to this exact checkout and every registered physical file is absent; otherwise it stops for inspection |
| “being used by another process” for an `.mdf` or port 5170 | Another app/tool still owns it | Identify and stop only the exact DigiChat/SQL tool process you own; do not delete around the lock or stop the shared instance |

## Locate LocalDB without stopping it

Try:

```text
sqllocaldb info
```

If the command is not on `PATH`, SQL Server 2022 normally installs:

```text
C:\Program Files\Microsoft SQL Server\160\Tools\Binn\SqlLocalDB.exe
```

`MSSQLLocalDB` is shared by every LocalDB project for this Windows user. Do not
stop the whole instance as DigiChat cleanup or first-line recovery; that
interrupts unrelated projects. Normal DigiChat shutdown disposes its
connections and should release its file use while the instance remains running.
If a file operation is still blocked, inspect the exact registration below and
use a targeted detach only when that operation genuinely requires one.

## Inspect registrations without `sqlcmd`

Run this from PowerShell. It reads the LocalDB master catalog and changes
nothing:

```powershell
$connection = New-Object System.Data.SqlClient.SqlConnection(
  'Server=(localdb)\MSSQLLocalDB;Database=master;Trusted_Connection=True;'
)
$connection.Open()
$command = $connection.CreateCommand()
$command.CommandText = @'
SELECT d.name, mf.physical_name, d.state_desc
FROM sys.databases AS d
LEFT JOIN sys.master_files AS mf ON mf.database_id = d.database_id
ORDER BY d.name, mf.file_id;
'@
$reader = $command.ExecuteReader()
while ($reader.Read()) {
  '{0} -> {1} ({2})' -f $reader.GetString(0), $reader.GetValue(1), $reader.GetString(2)
}
$reader.Close()
$connection.Close()
```

If `sqlcmd` is installed on another machine, it is also acceptable. Do not assume
it exists merely because LocalDB does.

## Automatic pre-migration backup

Before applying pending EF migrations to an **existing, connectable** SQL Server
database, startup asks SQL Server to create:

```text
data/db/backups/<catalog>-pre-migration-<UTC yyyyMMdd-HHmmssfff>.bak
```

The backup command uses `COPY_ONLY`, `INIT`, and `CHECKSUM`. Startup then runs
`RESTORE VERIFYONLY FROM DISK ... WITH CHECKSUM` and confirms that the file
exists and is non-empty. If backup or verification fails, the migration and
application startup stop. A brand-new database, an
unreachable/unregistered database, or an existing database with no pending
migration does not trigger this backup.

This is a last-known-schema recovery point, not a complete backup program:

- it remains inside the ignored project tree and is not copied off-device;
- there is no automatic retention or cleanup;
- it contains the same viewer history as the database and must not be committed
  or shared;
- `RESTORE VERIFYONLY` is not a test restore to the intended data/log files.

For a release or a valuable live history, copy the new `.bak` to a dated secure
location outside the repository. Startup already validates the original file;
after copying it, the same read-only SQL check can validate the off-project copy
by replacing the example path below:

```powershell
$backupPath = (Resolve-Path -LiteralPath 'D:\secure-backups\DigiChat-pre-migration-YYYYMMDD-HHMMSSfff.bak').Path
$connection = New-Object System.Data.SqlClient.SqlConnection(
  'Server=(localdb)\MSSQLLocalDB;Database=master;Trusted_Connection=True;'
)
$connection.Open()
try {
  $command = $connection.CreateCommand()
  $command.CommandText = 'RESTORE VERIFYONLY FROM DISK = @path WITH CHECKSUM;'
  $null = $command.Parameters.AddWithValue('@path', $backupPath)
  $null = $command.ExecuteNonQuery()
} finally {
  $connection.Close()
}
```

`RESTORE VERIFYONLY` improves confidence that SQL Server can read the backup; it
still does not prove a future restore to the intended files.

## Manual offline backup

1. Stop the exact DigiChat process and confirm port 5170 is free. Leave the
   shared LocalDB instance running.
2. Use the read-only master-catalog query above to confirm the target catalog,
   primary `.mdf`, and log path all belong to this checkout.
3. Copy the exact target `.mdf` and matching log file, plus any applicable
   pre-migration `.bak`, to a dated backup location outside the repository.
   Normal disposed connections should allow this without detaching anything.
4. If the copy is blocked by LocalDB after all owned connections are closed, do
   not stop the instance. Follow “Targeted detach” below for only the inspected
   DigiChat catalog, copy both files, then reattach that same pair immediately.
5. Confirm the copies have the expected names and nonzero sizes. Start the
   intended mode and verify its stage, generation, session, and participants
   before treating the backup as complete.

Do not commit a backup. Database files contain viewer data and are gitignored.
Copying all of `data/db` is acceptable only when the destination is private and
you intentionally want both live and mock histories; otherwise target the
matching pair by exact name.

## Reset mock data

The least surprising reset is manual and recoverable:

1. Stop the exact DigiChat process, confirm port 5170 is free, and leave the
   shared LocalDB instance running.
2. If the mock state might matter, copy its two files to a backup folder.
3. Inspect the master catalog and confirm `DigiChat_Mock` points to this
   checkout's exact mock `.mdf` and log paths.
4. In File Explorer, move only `DigiChat.Mock.mdf` and its matching log file out
   of `data/db`; do not use a wildcard and do not touch `DigiChat.mdf`. Normal
   application shutdown should make this possible. If it remains locked, use a
   targeted detach of the already-inspected `DigiChat_Mock` catalog; never stop
   the shared instance.
5. Start mock mode. If the catalog remained registered,
   `DatabaseLocation.DropStaleRegistrationAsync` clears the
   stale `DigiChat_Mock` catalog only if its primary data file points to this
   exact checkout and **all** catalog files are absent. If the log file was left
   behind or the catalog belongs to another checkout, startup refuses the drop.
   If a targeted detach was required, there is no stale registration to clear.
   In either case, migrations create a fresh mock database.
6. After successful verification, the moved mock backup can be deleted.

Moving first keeps recovery possible if the wrong state was identified. A live
reset follows the same mechanics only after explicit approval and backup.

## Restore a backup

1. Stop the exact DigiChat process, confirm port 5170 is free, and leave the
   shared LocalDB instance running.
2. Inspect the target catalog and its exact physical paths. Back up the current
   pair, then move it aside; do not overwrite files in place. If a move is still
   blocked after owned connections close, use a targeted detach of only that
   inspected catalog.
3. Copy the matching backed-up `.mdf` and log file into `data/db` under their
   original names.
4. If the target catalog remains registered to those exact paths, start only
   the matching mode and inspect startup logs. If it was detached or otherwise
   unregistered, attach the restored pair to the exact intended catalog first;
   starting with files but no registration produces SQL 5170.
5. Confirm current stage, generation, session, and participants before removing
   the files moved aside.

Never restore live files while mock mode is running, or vice versa.

An automatic `.bak` is a SQL Server backup rather than a raw file pair. Restoring
one is destructive to the target catalog and requires explicit human approval,
an offline copy of the current target files, and `RESTORE FILELISTONLY` review.
The backup's catalog and recorded physical paths must match the intended mode
and checkout; a different path requires reviewed `WITH MOVE` destinations.
Use SSMS or a targeted master-database connection, restore with `CHECKSUM`, then
confirm the catalog is back in `MULTI_USER` mode. A pre-migration backup contains
the previous schema, so the next DigiChat start may back it up again and reapply
the pending migration. Do not substitute a guessed `RESTORE ... WITH REPLACE`
command for those checks.

## Targeted detach (only when a file operation is blocked)

Detach changes LocalDB catalog state. It is not shutdown cleanup and must never
be used merely because `MSSQLLocalDB` is running.

Before detaching:

1. Stop the exact DigiChat process and any SQL tool connected to the target.
2. Run the master-catalog query above and record the exact catalog plus every
   physical path. Stop if they do not belong to this checkout and intended mode.
3. Make or identify a verified backup. For `DigiChat`, obtain explicit human
   approval before the detach.
4. Connect to LocalDB `master` and detach **only** the inspected catalog, using
   SSMS, `sqlcmd` if already installed, or a reviewed PowerShell SQL command.
   Do not stop the LocalDB instance and do not detach any catalog by pattern.

If preserving/restoring the files, reattach the exact `.mdf` and log pair to the
same catalog immediately after the file operation and re-run the read-only
catalog query. If resetting mock data, leave only `DigiChat_Mock` detached and
both old mock files moved aside; mock startup will create the new catalog. An
unregistered existing `.mdf` must be explicitly attached—DigiChat will preserve
it and report SQL 5170 rather than guessing that it is safe.

## Manual catalog changes are the last resort

`DatabaseLocation.DropStaleRegistrationAsync` already handles a registration
only when its expected primary file points to this checkout and every registered
physical file is genuinely missing. An inaccessible path is treated as unknown,
not missing. A catalog from another checkout, any surviving physical file, a
file-inspection failure, or a failed verified drop stops startup without further
database action.

If automatic cleanup reports that it could not clear a verified stale
registration, preserve the full error and inspect the master catalog again. A
manual `DROP DATABASE` is a human-approved last resort; current startup does
not print a copy-paste statement because doing so would encourage bypassing the
path checks.

Before executing any `DROP DATABASE`, verify all four facts:

- the catalog name is exactly `DigiChat_Mock` or `DigiChat` as intended;
- every registered data/log file and its path have been inspected, and the
  corresponding files are genuinely absent or safely backed up;
- the registration is not owned by another DigiChat checkout;
- for `DigiChat`, a human explicitly approved loss or replacement of live
  history.

For SQL 5170 with an existing file, do the opposite: preserve it and attach or
restore its registration. Do not solve “file exists” by deleting an uninspected
live database.

## After an agent-run session

Stop the exact server process and verify port 5170 is free. Leave the shared
`MSSQLLocalDB` instance and catalog registrations alone; normal disposed
connections should release DigiChat's file use. If a later file operation is
blocked, return to read-only catalog/path inspection instead of stopping the
instance.
