using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using WinRT.Interop;
using global::Windows.Services.Store;

namespace ImmichDrive.Services;

/// <summary>
/// Manual update check against this repo's GitHub Releases. Runs only when the user
/// clicks "Check for updates" — there is no automatic/background network activity.
/// (Store-installed copies also update automatically through the Microsoft Store.)
/// </summary>
public static class UpdateService
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private const string Owner = "RyanEwen";
    private const string Repo = "ImmichDrive";

    /// <summary>Store product ID for Drive for Immich (the ID in its Store listing URL).</summary>
    private const string StoreProductId = "9MWC6165N7DH";
    private static readonly Uri LatestReleaseUri = new($"https://api.github.com/repos/{Owner}/{Repo}/releases/latest");

    private static readonly HttpClient Http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"ImmichDrive/{CurrentVersion()}");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    public sealed class UpdateCheckResult
    {
        public bool UpdateAvailable { get; init; }
        public string CurrentVersion { get; init; } = "";
        public string LatestVersion { get; init; } = "";
        public string? ReleaseUrl { get; init; }

        /// <summary>
        /// True when the Microsoft Store owns this install, so updates come from there rather
        /// than a GitHub release. Callers use it to decide whether to offer an in-app install
        /// or a link.
        /// </summary>
        public bool IsStoreManaged { get; init; }
    }

    public static string CurrentVersion()
    {
        var v = typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0);
        return $"{v.Major}.{v.Minor}.{v.Build}";
    }

    /// <summary>Returns null only on a network/parse error; an up-to-date result when there are no newer releases.</summary>
    /// <summary>
    /// Check for an update from whichever channel actually installed this copy.
    /// </summary>
    public static Task<UpdateCheckResult?> CheckForUpdateAsync() =>
        IsPackaged ? CheckForStoreUpdateAsync() : CheckGitHubForUpdateAsync();

    /// <summary>
    /// Ask the Store what it has for this package.
    /// </summary>
    /// <remarks>
    /// <para><c>GetAppAndOptionalStorePackageUpdatesAsync</c> lists every package the Store can
    /// update, including framework dependencies, so the app's own package is picked out by family
    /// name. Its presence in that list <b>is</b> the "an update is waiting for you" signal.</para>
    /// <para><b>What it is not is a source of the new version number.</b>
    /// <c>StorePackageUpdate.Package</c> describes the package <i>as installed</i>, so
    /// <c>Id.Version</c> reports the version already on the machine, never the one being offered.
    /// Measured on a sibling app with a live Store update pending (installed 1.27.1.0, published
    /// 1.28.0.0): the list held exactly one entry, the app's own, reporting 1.27.1.0. Requiring
    /// the listed version to be strictly newer — an earlier attempt to stop the UI offering an
    /// update to the running version — therefore never matched, and this reported "up to date"
    /// permanently while the Store showed the update ready to install. <b>Do not reintroduce that
    /// comparison.</b></para>
    /// <para>The number, and the guard that comparison was reaching for, come from the Store
    /// catalog instead — see <see cref="TryGetPublishedVersionAsync"/>.</para>
    /// </remarks>
    private static async Task<UpdateCheckResult?> CheckForStoreUpdateAsync()
    {
        try
        {
            var context = StoreContext.GetDefault();
            var updates = await context.GetAppAndOptionalStorePackageUpdatesAsync();

            var package = global::Windows.ApplicationModel.Package.Current;
            var currentVersion = ToVersion(package.Id.Version);
            string family = package.Id.FamilyName;

            bool ourPackageListed = updates.Any(u => u.Package != null && string.Equals(
                u.Package.Id.FamilyName, family, StringComparison.OrdinalIgnoreCase));

            // Best-effort: null means "cannot say", and the Store's list is then trusted on its
            // own rather than being vetoed by a failed lookup. A published version that is not
            // newer suppresses the offer, which is the stale-list guard done correctly.
            Version? published = ourPackageListed ? await TryGetPublishedVersionAsync() : null;
            bool publishedIsNewer = published != null && published > currentVersion;
            bool available = ourPackageListed && (published == null || publishedIsNewer);

            // Logged on every check because the failure this replaced was invisible: the check
            // succeeded, so nothing was written, and "up to date" was indistinguishable from a bug.
            Logger.Info(
                "Store update check: {Count} package update(s) listed, ours present: {Ours}, "
                + "installed {Current}, published {Published}, update available: {Available}",
                updates.Count, ourPackageListed, currentVersion,
                published?.ToString() ?? "unknown", available);

            return new UpdateCheckResult
            {
                IsStoreManaged = true,
                UpdateAvailable = available,

                // Empty means "there is a newer version but its number is not known" — the UI
                // words that case without a version rather than inventing one.
                CurrentVersion = currentVersion.ToString(3),
                LatestVersion = available
                    ? (publishedIsNewer ? published!.ToString(3) : "")
                    : currentVersion.ToString(3),
            };
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Store update check failed");
            return null;
        }
    }

    /// <summary>
    /// Reads the version currently published to the Store for this product, or null if it cannot
    /// be determined. Uses the Store's public display-catalog endpoint, which is what the Store
    /// client itself reads; there is no WinRT API that reports a pending update's version.
    /// </summary>
    private static async Task<Version?> TryGetPublishedVersionAsync()
    {
        try
        {
            var uri = new Uri(
                $"https://displaycatalog.mp.microsoft.com/v7.0/products/{StoreProductId}"
                + "?market=US&languages=en-us&fieldsTemplate=Details");

            using var stream = await Http.GetStreamAsync(uri);
            using var doc = await System.Text.Json.JsonDocument.ParseAsync(stream);

            string arch = global::Windows.ApplicationModel.Package.Current.Id.Architecture switch
            {
                global::Windows.System.ProcessorArchitecture.Arm64 => "arm64",
                global::Windows.System.ProcessorArchitecture.X86 => "x86",
                _ => "x64",
            };

            Version? best = null;

            if (!doc.RootElement.TryGetProperty("Product", out var product)
                || !product.TryGetProperty("DisplaySkuAvailabilities", out var skus))
            {
                return null;
            }

            foreach (var sku in skus.EnumerateArray())
            {
                if (!sku.TryGetProperty("Sku", out var skuInfo)
                    || !skuInfo.TryGetProperty("Properties", out var props)
                    || !props.TryGetProperty("Packages", out var packages))
                {
                    continue;
                }

                foreach (var p in packages.EnumerateArray())
                {
                    // The catalog's numeric "Version" is a packed 64-bit value; the version in
                    // human form only appears inside the package full name
                    // (Name_0.1.40.0_arm64__hash), which is also where the architecture is.
                    if (p.TryGetProperty("PackageFullName", out var fullNameElement)
                        && fullNameElement.GetString() is { } fullName)
                    {
                        var parts = fullName.Split('_');
                        if (parts.Length < 3) continue;
                        if (!parts[2].Equals(arch, StringComparison.OrdinalIgnoreCase)) continue;
                        if (!Version.TryParse(parts[1], out var parsed)) continue;
                        if (best == null || parsed > best) best = parsed;
                    }
                }
            }

            return best;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to read the published Store version");
            return null;
        }
    }

    private static Version ToVersion(global::Windows.ApplicationModel.PackageVersion v) =>
        new(v.Major, v.Minor, v.Build, v.Revision);

    /// <summary>
    /// Download and install a Store update, then close so it can finish.
    /// </summary>
    /// <remarks>
    /// <para><b>Download and install are separate calls on purpose.</b>
    /// <c>RequestDownloadStorePackageUpdatesAsync</c> runs while the app is in use and does not
    /// block, which is the only place real progress can come from. Installing needs every process
    /// in the package to exit. Calling the combined API from a tray app that never closes hides
    /// the download behind a wait that cannot resolve, so it sits on "waiting to close app"
    /// showing nothing.</para>
    /// <para>Drive for Immich lives in the tray and starts with Windows, so this is the normal
    /// case for it, not an edge case.</para>
    /// </remarks>
    public static async Task<(bool Success, string Message)> DownloadAndInstallStoreUpdateAsync(
        nint ownerWindowHandle,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var context = StoreContext.GetDefault();
            if (ownerWindowHandle != 0)
                InitializeWithWindow.Initialize(context, ownerWindowHandle);

            var updates = await context.GetAppAndOptionalStorePackageUpdatesAsync();
            if (updates.Count == 0)
            {
                // Nothing to download can mean up to date, or that Windows already staged the
                // update and is waiting for this app to exit. The list stops reporting a package
                // once it is staged, so the two are indistinguishable from here. Offering the
                // restart is right either way: it costs a relaunch if wrong and completes a
                // stuck update if right.
                return (false,
                    "No download is pending. If an update was already downloaded in the "
                    + "background, restart Drive for Immich to finish installing it.");
            }

            var download = context.RequestDownloadStorePackageUpdatesAsync(updates);
            download.Progress = (_, status) =>
                progress?.Report(Math.Clamp(status.PackageDownloadProgress, 0.0, 1.0));

            var downloaded = await download;
            if (downloaded.OverallState is not (StorePackageUpdateState.Completed
                or StorePackageUpdateState.Deploying))
            {
                return (false, DescribeStoreUpdateState(downloaded.OverallState));
            }

            progress?.Report(1.0);

            // Must be registered before shutdown begins, not during it.
            RegisterApplicationRestart(null, 0);

            var install = context.RequestDownloadAndInstallStorePackageUpdatesAsync(updates);
            var result = await install;

            return result.OverallState switch
            {
                StorePackageUpdateState.Completed or StorePackageUpdateState.Deploying =>
                    (true, "Update ready. Drive for Immich will close to finish installing."),
                _ => (false, DescribeStoreUpdateState(result.OverallState)),
            };
        }
        catch (OperationCanceledException)
        {
            return (false, "Update was cancelled.");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Store update failed");
            return (false, $"Microsoft Store update failed: {ex.Message}");
        }
    }

    private static string DescribeStoreUpdateState(StorePackageUpdateState state) => state switch
    {
        StorePackageUpdateState.Canceled => "Update was cancelled in the Microsoft Store dialog.",
        StorePackageUpdateState.ErrorLowBattery => "Update paused because the device battery is too low.",
        StorePackageUpdateState.ErrorWiFiRecommended => "Update was paused because a non-metered connection is recommended.",
        StorePackageUpdateState.ErrorWiFiRequired => "Update requires Wi-Fi before the Microsoft Store can continue.",
        _ => "The Microsoft Store could not install the update. Try again later.",
    };

    private static async Task<UpdateCheckResult?> CheckGitHubForUpdateAsync()
    {
        try
        {
            using var resp = await Http.GetAsync(LatestReleaseUri);
            // No releases published yet → you already have the latest.
            if (resp.StatusCode == HttpStatusCode.NotFound)
                return new UpdateCheckResult { CurrentVersion = CurrentVersion(), LatestVersion = CurrentVersion() };

            resp.EnsureSuccessStatusCode();
            var release = await resp.Content.ReadFromJsonAsync(GitHubJsonContext.Default.GitHubRelease);
            if (release == null || string.IsNullOrWhiteSpace(release.TagName)) return null;

            var current = ParseVersion(CurrentVersion());
            var latest = ParseVersion(release.TagName);
            if (current == null || latest == null) return null;

            return new UpdateCheckResult
            {
                IsStoreManaged = false,
                UpdateAvailable = latest > current,
                CurrentVersion = current.ToString(3),
                LatestVersion = latest.ToString(3),
                ReleaseUrl = release.HtmlUrl,
            };
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Update check failed");
            return null;
        }
    }

    /// <summary>
    /// True when running from an MSIX package, so the Microsoft Store owns updating.
    /// </summary>
    /// <remarks>
    /// <c>Package.Current</c> throws rather than returning null when the process is unpackaged,
    /// which is the documented way to tell the two apart. Cached because the answer cannot
    /// change while the process lives.
    /// </remarks>
    public static bool IsPackaged { get; } = DetectPackaged();

    private static bool DetectPackaged()
    {
        try
        {
            return global::Windows.ApplicationModel.Package.Current is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Close so Windows can finish installing a Store update, and come back afterwards.
    /// </summary>
    /// <remarks>
    /// <para><b>An MSIX package cannot be installed while any of its processes are running.</b>
    /// Drive for Immich lives in the tray and starts with Windows, so a Store update downloads,
    /// stages, and then waits for an exit that never comes. The user sees nothing at all: the
    /// Store reports the app as up to date on its own schedule while the new version sits
    /// unapplied, sometimes for weeks.</para>
    /// <para>Restarting is the whole fix, and it needs no update APIs. Windows applies anything
    /// staged the moment the last process exits, so this works whether or not an update is
    /// actually pending, and does not depend on being able to detect one. That matters because
    /// there is no reliable way to ask: <c>Package.CheckUpdateAvailabilityAsync</c> only covers
    /// .appinstaller installs, not Store-distributed packages.</para>
    /// <para><c>RegisterApplicationRestart</c> has to be called before shutdown begins, not
    /// during it.</para>
    /// </remarks>
    public static void RestartToApplyUpdates()
    {
        RegisterApplicationRestart(null, 0);
        Microsoft.UI.Xaml.Application.Current.Exit();
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int RegisterApplicationRestart(string? pwzCommandline, int dwFlags);

    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Logger.Warn(ex, "Failed to open {Url}", url); }
    }

    private static Version? ParseVersion(string s)
    {
        s = s.Trim().TrimStart('v', 'V');
        return Version.TryParse(s, out var v) ? v : null;
    }
}

internal sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")] public string? TagName { get; set; }
    [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
}

[JsonSerializable(typeof(GitHubRelease))]
internal partial class GitHubJsonContext : JsonSerializerContext { }
