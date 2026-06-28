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
| `ServerUrl` | Immich base URL (no trailing `/api`). |
| `ApiKey` | Immich API key (sent as `x-api-key`). Stored locally. |
| `Connected` | Whether the sync root is currently registered/connected. |
| `SyncRootPath` | Local folder presented as the drive. Empty = default `%UserProfile%\ImmichDrive`. |
| `EffectiveSyncRootPath` | `[JsonIgnore]` computed: `SyncRootPath` or the default if unset. |
| `LastSyncUtc` | Timestamp of the last successful timeline populate. |
| `LayoutVersion` | On-disk layout/naming revision; a bump triggers a one-time clean rebuild. |
| `RegisteredVersion` | App version the sync root was last registered with; a change re-registers to refresh the icon. |
| `AppTheme` | 0 = system, 1 = light, 2 = dark. |
| `Startup` | Launch ImmichDrive at sign-in (keeps the drive online). Defaults to `true`. |
| `LastKnownVersion` | For update/"what's new" detection. |
| `SettingsWindowX/Y/Width/Height` | Window geometry restore. |

The drive is always read-only; there is no `ReadOnlyDrive` setting. There is no `Recent\` folder
and no `RecentDays` setting.

## Startup

`Startup` defaults to `true`. Its `OnStartupChanged` side-effect calls
`StartupManager.SetRunAtStartup`, which enables/disables the MSIX `windows.startupTask` (declared
in the manifest and enabled by default), falling back to the per-user `Run` key for unpackaged dev
builds. `SettingsPage` initializes its toggle from `StartupManager.IsEnabled()` — the real OS state
— not the stored flag, so it stays truthful even if the user changed it in Task Manager.

> `ApiKey` is a secret stored in plaintext JSON like the rest of settings. If hardening is
> wanted later, move it to the Windows Credential Manager / DPAPI and keep only a reference here.
