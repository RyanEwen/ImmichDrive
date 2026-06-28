using CommunityToolkit.Mvvm.ComponentModel;
using ImmichDrive.Services;
using System.IO;
using System.Text.Json.Serialization;

namespace ImmichDrive.ViewModels;

/// <summary>
/// All user settings. Every <c>[ObservableProperty]</c> auto-serializes to settings.json via
/// <see cref="ImmichDrive.Classes.Settings.SettingsManager"/>. Side-effects live in partial
/// <c>On…Changed</c> methods guarded by <see cref="_initializing"/>. See
/// <c>.claude/docs/user-settings.md</c>.
/// </summary>
public partial class UserSettings : ObservableObject
{
    /// <summary>Suppresses side-effects while deserializing.</summary>
    [JsonIgnore] private bool _initializing = true;

    // ── Connection ───────────────────────────────────────────────────
    /// <summary>Immich base URL (without the trailing /api).</summary>
    [ObservableProperty] public partial string ServerUrl { get; set; } = "";

    /// <summary>Immich API key (sent as x-api-key). Stored locally in plaintext JSON.</summary>
    [ObservableProperty] public partial string ApiKey { get; set; } = "";

    /// <summary>Whether the drive should be connected/registered.</summary>
    [ObservableProperty] public partial bool Connected { get; set; }

    // ── Drive ────────────────────────────────────────────────────────
    /// <summary>Folder presented as the drive. Empty = default %UserProfile%\ImmichDrive.</summary>
    [ObservableProperty] public partial string SyncRootPath { get; set; } = "";

    /// <summary>Size of the flat Recent\ window in days.</summary>
    [ObservableProperty] public partial int RecentDays { get; set; } = 14;

    /// <summary>Last successful timeline populate (UTC).</summary>
    [ObservableProperty] public partial DateTimeOffset LastSyncUtc { get; set; }

    /// <summary>On-disk layout/naming revision; a bump triggers a one-time clean rebuild.</summary>
    [ObservableProperty] public partial int LayoutVersion { get; set; }

    /// <summary>App version the sync root was last registered with; a change re-registers to refresh the icon.</summary>
    [ObservableProperty] public partial string RegisteredVersion { get; set; } = "";

    // ── App ──────────────────────────────────────────────────────────
    /// <summary>0 = system, 1 = light, 2 = dark.</summary>
    [ObservableProperty] public partial int AppTheme { get; set; }

    /// <summary>Launch ImmichDrive at sign-in (keeps the drive online). On by default — matches the
    /// MSIX startup task, which is enabled by default, so the drive is ready and syncing after a boot.</summary>
    [ObservableProperty] public partial bool Startup { get; set; } = true;

    [ObservableProperty] public partial string LastKnownVersion { get; set; } = "";

    // ── Settings window geometry ─────────────────────────────────────
    [ObservableProperty] public partial int SettingsWindowX { get; set; }
    [ObservableProperty] public partial int SettingsWindowY { get; set; }
    [ObservableProperty] public partial int SettingsWindowWidth { get; set; }
    [ObservableProperty] public partial int SettingsWindowHeight { get; set; }

    /// <summary>The resolved sync-root folder (default if unset).</summary>
    [JsonIgnore]
    public string EffectiveSyncRootPath =>
        string.IsNullOrWhiteSpace(SyncRootPath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ImmichDrive")
            : SyncRootPath;

    // ── Side-effects ─────────────────────────────────────────────────
    partial void OnAppThemeChanged(int value)
    {
        if (_initializing) return;
        Classes.ThemeManager.ApplyAndSaveTheme(value);
    }

    partial void OnStartupChanged(bool value)
    {
        if (_initializing) return;
        Classes.StartupManager.SetRunAtStartup(value);
    }

    /// <summary>Repairs nulls from older/partial files and ends the initializing window.</summary>
    public void CompleteInitialization()
    {
        ServerUrl ??= "";
        ApiKey ??= "";
        SyncRootPath ??= "";
        if (RecentDays <= 0) RecentDays = 14;
        _initializing = false;
    }
}
