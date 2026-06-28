# Settings conventions

`UserSettings : ObservableObject` (`ViewModels/UserSettings.cs`) holds all settings as
`[ObservableProperty]` partial properties; `SettingsManager` (static) serializes it to
`%AppData%\ImmichDrive\settings.json` (System.Text.Json, PascalCase, `WhenWritingDefault`).

## Conventions

- One `[ObservableProperty]` per setting, PascalCase, with a sensible default.
- Side-effects go in a partial `On<Name>Changed(value)`, guarded by `if (_initializing) return;`
  so loading the file doesn't fire them. `CompleteInitialization()` repairs nulls and flips
  `_initializing` off at the end of restore.
- `[JsonIgnore]` for runtime-only / computed properties.
- Bind UI `Mode=TwoWay` to `SettingsManager.Current.<Name>`; call `SettingsManager.SaveSettings()`
  after meaningful edits.

## Current settings

| Property | Meaning |
|---|---|
| `AppTheme` | 0 = system, 1 = light, 2 = dark. |
| `Startup` | Launch ImmichDrive at sign-in (keeps the drive online). |
| `ServerUrl` | Immich base URL (no trailing `/api`). |
| `ApiKey` | Immich API key (sent as `x-api-key`). Stored locally. |
| `SyncRootPath` | Local folder presented as the drive (default `%UserProfile%\ImmichDrive`). |
| `RecentDays` | Size of the flat `Recent\` window in days (default 14). |
| `Connected` | Whether the sync root is currently registered/connected. |
| `LastSyncUtc` | Timestamp of the last successful timeline populate. |
| `LastKnownVersion` | For update/"what's new" detection. |
| `SettingsWindowX/Y/Width/Height` | Window geometry restore. |

> `ApiKey` is a secret stored in plaintext JSON like the rest of settings. If hardening is
> wanted later, move it to the Windows Credential Manager / DPAPI and keep only a reference here.
