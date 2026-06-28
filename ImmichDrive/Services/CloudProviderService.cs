using System.Runtime.InteropServices;
using System.Text;
using static ImmichDrive.Classes.CloudFilter.CfApi;

namespace ImmichDrive.Services;

/// <summary>
/// The live Cloud Files sync provider: connects the cfapi callback table to the sync root and
/// hydrates placeholders on demand by streaming originals from Immich. Holds the connection
/// for the lifetime of the resident app. See <c>.claude/docs/cloud-files.md</c>.
/// </summary>
public sealed class CloudProviderService : IDisposable
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
    private const int ChunkBytes = 1 << 20; // 1 MiB — a multiple of the disk sector size

    private readonly ImmichClient _client;
    private long _connectionKey;
    private bool _connected;

    // Keep the delegates + table rooted so the GC can't collect them while the OS holds them.
    private CF_CALLBACK_REGISTRATION[]? _table;
    private CF_CALLBACK? _onFetchData;
    private CF_CALLBACK? _onCancelFetchData;

    public CloudProviderService(ImmichClient client) => _client = client;

    public void Connect(string syncRootPath)
    {
        if (_connected) return;

        _onFetchData = OnFetchData;
        _onCancelFetchData = OnCancelFetchData;
        _table =
        [
            new CF_CALLBACK_REGISTRATION { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_FETCH_DATA, Callback = _onFetchData },
            new CF_CALLBACK_REGISTRATION { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_CANCEL_FETCH_DATA, Callback = _onCancelFetchData },
            new CF_CALLBACK_REGISTRATION { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NONE, Callback = null! },
        ];

        int hr = CfConnectSyncRoot(
            syncRootPath, _table, IntPtr.Zero,
            CF_CONNECT_FLAGS.CF_CONNECT_FLAG_REQUIRE_PROCESS_INFO | CF_CONNECT_FLAGS.CF_CONNECT_FLAG_REQUIRE_FULL_FILE_PATH,
            out _connectionKey);

        if (hr < 0)
        {
            Logger.Error("CfConnectSyncRoot failed: 0x{0:X8}", hr);
            throw Marshal.GetExceptionForHR(hr) ?? new InvalidOperationException($"CfConnectSyncRoot 0x{hr:X8}");
        }
        _connected = true;
        Logger.Info("Connected sync root at {0}", syncRootPath);
    }

    public void Disconnect()
    {
        if (!_connected) return;
        CfDisconnectSyncRoot(_connectionKey);
        _connected = false;
        _table = null;
        _onFetchData = null;
        _onCancelFetchData = null;
    }

    // ── Callbacks (invoked on OS threads) ───────────────────────────
    private void OnFetchData(IntPtr callbackInfoPtr, IntPtr callbackParamsPtr)
    {
        try
        {
            var info = Marshal.PtrToStructure<CF_CALLBACK_INFO>(callbackInfoPtr);
            var fetch = Marshal.PtrToStructure<CF_CALLBACK_PARAMETERS_FETCHDATA>(callbackParamsPtr);

            string assetId = ReadFileIdentity(info);
            long connKey = info.ConnectionKey;
            long transferKey = info.TransferKey;
            long requestKey = info.RequestKey;
            long offset = fetch.RequiredFileOffset;
            long length = fetch.RequiredLength;

            // Offload the actual transfer; the callback itself should return promptly.
            _ = Task.Run(() => HydrateAsync(assetId, connKey, transferKey, requestKey, offset, length));
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "OnFetchData failed");
        }
    }

    private void OnCancelFetchData(IntPtr callbackInfoPtr, IntPtr callbackParamsPtr)
    {
        // Cooperative cancellation hook — a production build would signal the matching
        // in-flight transfer (keyed by TransferKey) to stop. Left as a no-op stub.
    }

    private async Task HydrateAsync(string assetId, long connKey, long transferKey, long requestKey, long offset, long length)
    {
        if (string.IsNullOrEmpty(assetId))
        {
            Logger.Warn("Hydrate with empty asset id; failing fetch");
            TransferData(connKey, transferKey, requestKey, IntPtr.Zero, offset, 0, STATUS_UNSUCCESSFUL);
            return;
        }

        var buffer = new byte[ChunkBytes];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            using var resp = await _client.GetOriginalAsync(assetId, offset, length);
            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync();

            long pos = offset;
            long end = offset + length;
            while (pos < end)
            {
                int want = (int)Math.Min(ChunkBytes, end - pos);
                int read = await stream.ReadAtLeastAsync(buffer.AsMemory(0, want), want, throwOnEndOfStream: false);
                if (read <= 0) break;
                TransferData(connKey, transferKey, requestKey, handle.AddrOfPinnedObject(), pos, read, STATUS_SUCCESS);
                pos += read;
            }
            if (pos < end) // server returned less than promised
                TransferData(connKey, transferKey, requestKey, handle.AddrOfPinnedObject(), pos, 0, STATUS_UNSUCCESSFUL);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Hydration failed for asset {0}", assetId);
            // Tell the OS the fetch failed so the open returns an error instead of hanging.
            TransferData(connKey, transferKey, requestKey, handle.AddrOfPinnedObject(), offset, 0, STATUS_UNSUCCESSFUL);
        }
        finally
        {
            handle.Free();
        }
    }

    private static void TransferData(long connKey, long transferKey, long requestKey, IntPtr buffer, long offset, int length, int status)
    {
        var opInfo = new CF_OPERATION_INFO
        {
            StructSize = (uint)Marshal.SizeOf<CF_OPERATION_INFO>(),
            Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_DATA,
            ConnectionKey = connKey,
            TransferKey = transferKey,
            RequestKey = requestKey,
        };
        var opParams = new CF_OPERATION_PARAMETERS_TRANSFERDATA
        {
            ParamSize = (uint)Marshal.SizeOf<CF_OPERATION_PARAMETERS_TRANSFERDATA>(),
            CompletionStatus = status,
            Buffer = buffer,
            Offset = offset,
            Length = length,
        };
        int hr = CfExecute(in opInfo, ref opParams);
        if (hr < 0) Logger.Warn("CfExecute(TRANSFER_DATA) 0x{0:X8} at offset {1}", hr, offset);
    }

    private static string ReadFileIdentity(CF_CALLBACK_INFO info)
    {
        // The identity is a short UTF-8 GUID string; bound the length defensively so a bad value
        // can never read protected memory.
        if (info.FileIdentity == IntPtr.Zero || info.FileIdentityLength == 0 || info.FileIdentityLength > 4096)
            return "";
        var bytes = new byte[info.FileIdentityLength];
        Marshal.Copy(info.FileIdentity, bytes, 0, bytes.Length);
        return Encoding.UTF8.GetString(bytes).TrimEnd('\0');
    }

    public void Dispose() => Disconnect();
}
