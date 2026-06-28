using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ImmichDrive.Services;

/// <summary>
/// Makes the drive read-only to the user via a deny ACE for the current account (block create /
/// edit / delete / rename), while the cloud-files provider keeps working — cfapi placeholder and
/// hydration operations are performed by the cldflt filter and bypass the user's deny ACE (the same
/// way OneDrive's read-only folders behave). The <c>Upload</c> folder is excluded so the user can
/// drop files there. See <c>.claude/docs/read-only.md</c>.
/// </summary>
public static partial class DriveSecurity
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial void SHChangeNotify(int wEventId, uint uFlags, string dwItem1, IntPtr dwItem2);

    // Deny: Delete, DeleteChild, WriteData/AddFile, WriteAttributes, WriteEA. We intentionally do NOT
    // deny AddSubdirectory (AD) so the provider can still create month/album/partner folders with a
    // normal CreateDirectory (cfapi only bypasses the deny for file placeholders, not folder creation).
    // The only "leak" is that the user can make empty folders — but never put a file in one (WD denied).
    private const string DenyRights = "(OI)(CI)(DE,DC,WD,WA,WEA)";

    private static SecurityIdentifier CurrentUser => WindowsIdentity.GetCurrent().User!;
    private static string CurrentSid => CurrentUser.Value;

    /// <summary>Applies the read-only deny ACE to the sync root (inherited by all current + future items).</summary>
    public static void ApplyReadOnly(string syncRoot)
    {
        if (RunIcacls($"\"{syncRoot}\" /deny *{CurrentSid}:{DenyRights}"))
            Logger.Info("Applied read-only deny ACE to {0}", syncRoot);
    }

    /// <summary>Removes the read-only deny ACE (drive becomes writable again).</summary>
    public static void RemoveReadOnly(string syncRoot)
    {
        if (RunIcacls($"\"{syncRoot}\" /remove:d *{CurrentSid}"))
            Logger.Info("Removed read-only deny ACE from {0}", syncRoot);
    }

    /// <summary>Creates the Upload folder and makes it writable (breaks inheritance of the deny, grants full control).</summary>
    public static void EnsureUploadWritable(string uploadPath)
    {
        Directory.CreateDirectory(uploadPath);
        RunIcacls($"\"{uploadPath}\" /inheritance:r");
        RunIcacls($"\"{uploadPath}\" /grant *{CurrentSid}:(OI)(CI)F");
    }

    /// <summary>
    /// Gives the sync-root folder a custom icon in Explorer's file listing / This PC by writing a
    /// <c>desktop.ini</c> that points at the app .ico and flagging the folder ReadOnly (Explorer only
    /// honors desktop.ini on ReadOnly/System folders). Must run while the folder is writable — the
    /// read-only deny ACE blocks the write — so call this after <see cref="RemoveReadOnly"/> and before
    /// <see cref="ApplyReadOnly"/>.
    /// </summary>
    public static void SetFolderIcon(string folder, string icoPath)
    {
        try
        {
            string ini = Path.Combine(folder, "desktop.ini");
            if (File.Exists(ini)) File.SetAttributes(ini, FileAttributes.Normal);
            File.WriteAllText(ini, $"[.ShellClassInfo]\r\nIconResource={icoPath},0\r\nConfirmFileOp=0\r\n");
            File.SetAttributes(ini, FileAttributes.Hidden | FileAttributes.System);
            new DirectoryInfo(folder).Attributes |= FileAttributes.ReadOnly;
            SHChangeNotify(0x00001000, 0x0005, folder, IntPtr.Zero); // SHCNE_UPDATEDIR, SHCNF_PATHW
            Logger.Info("Set folder icon on {0}", folder);
        }
        catch (Exception ex) { Logger.Warn(ex, "SetFolderIcon failed for {0}", folder); }
    }

    /// <summary>Grants the current user delete on a single file so the provider can prune it despite the deny.</summary>
    public static void AllowDeleteFile(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            var sec = fi.GetAccessControl();
            sec.AddAccessRule(new FileSystemAccessRule(CurrentUser,
                FileSystemRights.Delete | FileSystemRights.Write, AccessControlType.Allow));
            fi.SetAccessControl(sec);
        }
        catch (Exception ex) { Logger.Debug(ex, "AllowDeleteFile {0}", path); }
    }

    /// <summary>Grants full control recursively so the provider can delete a folder subtree despite the deny.</summary>
    public static void AllowDeleteTree(string dir) =>
        RunIcacls($"\"{dir}\" /grant *{CurrentSid}:(OI)(CI)F /T /C /Q");

    private static bool RunIcacls(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("icacls", args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p == null) return false;
            p.WaitForExit(60000);
            if (p.ExitCode != 0) Logger.Warn("icacls {0} exited {1}", args, p.ExitCode);
            return p.ExitCode == 0;
        }
        catch (Exception ex) { Logger.Warn(ex, "icacls failed: {0}", args); return false; }
    }
}
