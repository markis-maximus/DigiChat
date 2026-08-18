# DigiChat runbooks

These are the canonical, tool-neutral operating procedures. Agent- or
editor-specific configuration should link here and add only its tool mapping.

| Runbook | Use it for |
|---|---|
| [VERIFY.md](VERIFY.md) | One-command clean-clone validation, CI parity, identity hygiene, and verified public publishing |
| [MOCK-OPERATION.md](MOCK-OPERATION.md) | Starting, driving, visually checking, and fully stopping mock mode |
| [DATABASE-RECOVERY.md](DATABASE-RECOVERY.md) | Backup, restore, mock reset, LocalDB locks, and SQL 5170/1801 recovery |

If code changes an operator step, path, prerequisite, failure mode, or safety
boundary, update the relevant runbook in the same change. Dated machine state
belongs in `docs/HANDOFF.md`, not here.

Three operational topics deliberately live outside this directory, because they
already have a natural owner and a second copy here would only drift:

| Topic | Owned by |
|---|---|
| Importing and replacing sprite art | README, "Adding real Digimon art" |
| Sizing, facing and per-form tuning | [../ASSET-TUNING.md](../ASSET-TUNING.md) |
| Digimon naming convention and renames | [../DECISIONS.md](../DECISIONS.md) |
