using ImmichDrive.Classes.CloudFilter;
using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace ImmichDrive.Services;

/// <summary>
/// Gives populated drive folders Cloud Files state so Explorer can show their sync badge.
/// Existing installations have ordinary directories, so those are converted in place.
/// </summary>
internal static partial class CloudFolderState
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private const uint WriteDac = 0x00040000;
    private const uint ShareReadWriteDelete = 0x00000007;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16,
        SetLastError = true)]
    private static partial SafeFileHandle OpenDirectory(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    /// <summary>
    /// Marks a completely enumerated folder in sync without granting the user content-write access.
    /// WRITE_DAC is allowed by the drive's read-only ACE; it does not permit adding files.
    /// </summary>
    public static bool MarkInSync(string path) => SetInSync(path, true);

    /// <summary>
    /// Sets a folder's sync state, converting an ordinary directory in place when necessary.
    /// Upload uses the pending state until all local files have been uploaded and removed.
    /// Returns false when the folder cannot be updated so callers can retry later.
    /// </summary>
    public static bool SetInSync(string path, bool inSync)
    {
        try
        {
            var directory = new DirectoryInfo(path);
            if (!directory.Exists || directory.LinkTarget != null) return false;

            bool isPlaceholder = (directory.Attributes & FileAttributes.ReparsePoint) != 0;
            using SafeFileHandle handle = OpenDirectory(path, WriteDac, ShareReadWriteDelete,
                IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
            if (handle.IsInvalid)
                throw new Win32Exception(Marshal.GetLastPInvokeError());

            var state = inSync
                ? CfApi.CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_IN_SYNC
                : CfApi.CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_NOT_IN_SYNC;
            var flags = inSync
                ? CfApi.CF_CONVERT_FLAGS.CF_CONVERT_FLAG_MARK_IN_SYNC
                : CfApi.CF_CONVERT_FLAGS.CF_CONVERT_FLAG_NONE;

            int hr = isPlaceholder
                ? CfApi.CfSetInSyncState(handle, state,
                    0, IntPtr.Zero)
                : CfApi.CfConvertToPlaceholder(handle, IntPtr.Zero, 0,
                    flags, IntPtr.Zero, IntPtr.Zero);
            if (hr < 0)
                throw new COMException($"Cloud folder state update failed for {path}", hr);

            DriveSecurity.NotifyFolderChanged(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or COMException)
        {
            Logger.Warn(ex, "Could not set folder sync state to {0}: {1}", inSync, path);
            return false;
        }
    }

    /// <summary>
    /// Reassert state after a folder pin finishes. Do not convert an ordinary directory here:
    /// only population knows when its contents have been completely enumerated.
    /// </summary>
    public static void RefreshExisting(string path)
    {
        try
        {
            var directory = new DirectoryInfo(path);
            if (directory.Exists && directory.LinkTarget == null &&
                (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                MarkInSync(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Debug(ex, "Could not refresh folder after pinning: {0}", path);
        }
    }
}
