using System.Runtime.InteropServices;

namespace ImmichDrive.Classes.CloudFilter;

/// <summary>
/// P/Invoke surface for the Windows Cloud Files API (<c>cldapi.dll</c>, <c>cfapi.h</c>).
/// Struct layouts mirror <c>cfapi.h</c> and must be verified on-device (a wrong offset
/// silently corrupts hydration). See <c>.claude/docs/cloud-files.md</c>.
/// </summary>
internal static partial class CfApi
{
    private const string Dll = "cldapi.dll";

    // ── Enums ───────────────────────────────────────────────────────
    [Flags]
    public enum CF_CONNECT_FLAGS : uint
    {
        CF_CONNECT_FLAG_NONE = 0,
        CF_CONNECT_FLAG_REQUIRE_PROCESS_INFO = 0x2,
        CF_CONNECT_FLAG_REQUIRE_FULL_FILE_PATH = 0x4,
    }

    public enum CF_CALLBACK_TYPE : uint
    {
        CF_CALLBACK_TYPE_FETCH_DATA = 0,
        CF_CALLBACK_TYPE_VALIDATE_DATA = 1,
        CF_CALLBACK_TYPE_CANCEL_FETCH_DATA = 2,
        CF_CALLBACK_TYPE_FETCH_PLACEHOLDERS = 3,
        CF_CALLBACK_TYPE_NONE = 0xffffffff,
    }

    [Flags]
    public enum CF_CREATE_FLAGS : uint { CF_CREATE_FLAG_NONE = 0, CF_CREATE_FLAG_STOP_ON_ERROR = 1 }

    [Flags]
    public enum CF_PLACEHOLDER_CREATE_FLAGS : uint
    {
        CF_PLACEHOLDER_CREATE_FLAG_NONE = 0,
        CF_PLACEHOLDER_CREATE_FLAG_DISABLE_ON_DEMAND_POPULATION = 1,
        CF_PLACEHOLDER_CREATE_FLAG_MARK_IN_SYNC = 2,
        CF_PLACEHOLDER_CREATE_FLAG_SUPERSEDE = 4,
        CF_PLACEHOLDER_CREATE_FLAG_ALWAYS_FULL = 8,
    }

    public enum CF_OPERATION_TYPE : uint
    {
        CF_OPERATION_TYPE_TRANSFER_DATA = 0,
        CF_OPERATION_TYPE_RETRIEVE_DATA = 1,
        CF_OPERATION_TYPE_ACK_DATA = 2,
        CF_OPERATION_TYPE_RESTART_HYDRATION = 3,
        CF_OPERATION_TYPE_TRANSFER_PLACEHOLDERS = 4,
        CF_OPERATION_TYPE_ACK_DEHYDRATE = 5,
        CF_OPERATION_TYPE_ACK_DELETE = 6,
        CF_OPERATION_TYPE_ACK_RENAME = 7,
    }

    [Flags]
    public enum CF_OPERATION_TRANSFER_DATA_FLAGS : uint { CF_OPERATION_TRANSFER_DATA_FLAG_NONE = 0 }

    // ── FILE_BASIC_INFO + CF_FS_METADATA ────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    public struct FILE_BASIC_INFO
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CF_FS_METADATA
    {
        public FILE_BASIC_INFO BasicInfo;
        public long FileSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CF_PLACEHOLDER_CREATE_INFO
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string RelativeFileName;
        public CF_FS_METADATA FsMetadata;
        public IntPtr FileIdentity;
        public uint FileIdentityLength;
        public CF_PLACEHOLDER_CREATE_FLAGS Flags;
        public int Result;            // HRESULT (out)
        public long CreateUsn;        // out
    }

    // ── Callback table ──────────────────────────────────────────────
    // void CALLBACK CF_CALLBACK(const CF_CALLBACK_INFO*, const CF_CALLBACK_PARAMETERS*)
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void CF_CALLBACK(IntPtr callbackInfo, IntPtr callbackParameters);

    [StructLayout(LayoutKind.Sequential)]
    public struct CF_CALLBACK_REGISTRATION
    {
        public CF_CALLBACK_TYPE Type;
        public CF_CALLBACK Callback;
    }

    // Field order matches cfapi.h EXACTLY (verified against the SDK header). A wrong order makes
    // FileIdentity point at garbage and crashes (access violation) during hydration.
    [StructLayout(LayoutKind.Sequential)]
    public struct CF_CALLBACK_INFO
    {
        public uint StructSize;
        public long ConnectionKey;
        public IntPtr CallbackContext;
        public IntPtr VolumeGuidName;     // LPCWSTR
        public IntPtr VolumeDosName;      // LPCWSTR
        public uint VolumeSerialNumber;
        public long SyncRootFileId;
        public IntPtr SyncRootIdentity;   // LPCVOID
        public uint SyncRootIdentityLength;
        public long FileId;
        public long FileSize;
        public IntPtr FileIdentity;       // LPCVOID
        public uint FileIdentityLength;
        public IntPtr NormalizedPath;     // LPCWSTR
        public long TransferKey;
        public byte PriorityHint;
        public IntPtr CorrelationVector;  // CORRELATION_VECTOR*
        public IntPtr ProcessInfo;        // CF_PROCESS_INFO*
        public long RequestKey;
    }

    // CF_CALLBACK_PARAMETERS is a union; ParamSize is followed by 4 bytes of padding so the
    // 8-byte-aligned union arm starts at offset 8. We only read the FetchData arm.
    [StructLayout(LayoutKind.Sequential)]
    public struct CF_CALLBACK_PARAMETERS_FETCHDATA
    {
        public uint ParamSize;
        public uint _pad0;                // union 8-byte alignment
        public uint Flags;                // CF_CALLBACK_FETCH_DATA_FLAGS
        public uint _pad1;                // align the LARGE_INTEGERs to 8
        public long RequiredFileOffset;
        public long RequiredLength;
        public long OptionalFileOffset;
        public long OptionalLength;
        public long LastDehydrationTime;
        public uint LastDehydrationReason;
    }

    // ── Operation (CfExecute) ───────────────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    public struct CF_OPERATION_INFO
    {
        public uint StructSize;
        public CF_OPERATION_TYPE Type;
        public long ConnectionKey;
        public long TransferKey;
        public long RequestKey;
        public IntPtr CorrelationVector;  // CORRELATION_VECTOR*
        public IntPtr SyncStatus;         // CF_SYNC_STATUS*
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CF_OPERATION_PARAMETERS_TRANSFERDATA
    {
        public uint ParamSize;
        public uint _pad0;                // union 8-byte alignment (arm starts at offset 8)
        public CF_OPERATION_TRANSFER_DATA_FLAGS Flags;
        public int CompletionStatus;      // NTSTATUS
        public IntPtr Buffer;
        public long Offset;
        public long Length;
    }

    public const int STATUS_SUCCESS = 0;
    public const int STATUS_UNSUCCESSFUL = unchecked((int)0xC0000001);

    // ── Functions ───────────────────────────────────────────────────
    [DllImport(Dll, CharSet = CharSet.Unicode)]
    public static extern int CfConnectSyncRoot(
        string syncRootPath,
        [MarshalAs(UnmanagedType.LPArray)] CF_CALLBACK_REGISTRATION[] callbackTable,
        IntPtr callbackContext,
        CF_CONNECT_FLAGS connectFlags,
        out long connectionKey);

    [DllImport(Dll)]
    public static extern int CfDisconnectSyncRoot(long connectionKey);

    [DllImport(Dll, CharSet = CharSet.Unicode)]
    public static extern int CfCreatePlaceholders(
        string baseDirectoryPath,
        [In, Out] CF_PLACEHOLDER_CREATE_INFO[] placeholderArray,
        uint placeholderCount,
        CF_CREATE_FLAGS createFlags,
        out uint entriesProcessed);

    [DllImport(Dll)]
    public static extern int CfExecute(in CF_OPERATION_INFO opInfo, ref CF_OPERATION_PARAMETERS_TRANSFERDATA opParams);

    [DllImport(Dll, CharSet = CharSet.Unicode)]
    public static extern int CfGetSyncRootInfoByPath(
        string filePath, int infoClass, IntPtr infoBuffer, uint infoBufferLength, out uint returnedLength);

    [DllImport(Dll)]
    public static extern int CfGetTransferKey(IntPtr fileHandle, out long transferKey);

    [DllImport(Dll)]
    public static extern int CfReleaseTransferKey(IntPtr fileHandle, ref long transferKey);
}
