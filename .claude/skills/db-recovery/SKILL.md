---
name: db-recovery
description: Diagnose DigiChat SQL 5170/1801 startup failures, attached-file locks, backup/restore, or LocalDB registration drift. Use after an agent-run server or whenever the app reports a database error.
---

# Claude adapter: database recovery

The canonical and safety-critical procedure is
`docs/runbooks/DATABASE-RECOVERY.md`. Read it completely before inspecting or
changing database state.

Claude-specific reminders:

- Start with read-only inspection. Preserve an existing `.mdf` until its mode,
  catalog, path, and value are known.
- The live `data/db/DigiChat.mdf` holds live stream history. Any detach, replacement,
  reset, or drop requires explicit approval and a verified backup.
- `DigiChat.Mock.mdf` is disposable, but still use exact paths and recoverable
  moves rather than wildcard deletion.
- Pending migrations of an existing, connectable LocalDB database create a
  sensitive `data/db/backups/*-pre-migration-*.bak` first. Backup failure blocks
  migration, and startup runs `RESTORE VERIFYONLY WITH CHECKSUM`; that is still
  not the same as a test restore. Preserve it off-project using the canonical
  runbook when live history matters.
- Automatic stale-registration cleanup refuses catalogs from another checkout
  and refuses to drop while any registered data or log file still exists. Do
  not work around either hard stop without the runbook's inspection and human
  approval boundary.
- Do not assume `sqlcmd` exists. The runbook contains a PowerShell
  `System.Data.SqlClient` inspection path.
- After any Claude preview that started the app, stop that exact preview and
  confirm port 5170 is free. Do not stop the shared `MSSQLLocalDB` instance.
  Detach only an exact inspected DigiChat catalog when the canonical recovery
  procedure genuinely requires moving its files.

Do not improvise destructive SQL from an error fragment. Follow the runbook's
5170/1801 decision table and manual-catalog safeguards.
