using System.Runtime.InteropServices;

namespace ImmichDrive.ThumbnailProvider;

/// <summary>Shell COM interop for the thumbnail handler. See <c>.claude/docs/thumbnails.md</c>.</summary>
internal static class ComGuids
{
    /// <summary>Our thumbnail provider CLSID — must match the MSIX manifest registration.</summary>
    public const string ThumbnailProviderClsid = "C0A6F3D2-7E41-4B8A-9C5E-2F1A6D3B8E70";
}

public enum WTS_ALPHATYPE
{
    WTSAT_UNKNOWN = 0,
    WTSAT_RGB = 1,
    WTSAT_ARGB = 2,
}

public enum SIGDN : uint
{
    SIGDN_FILESYSPATH = 0x80058000,
    SIGDN_NORMALDISPLAY = 0x00000000,
}

[ComImport, Guid("e357fccd-a995-4576-b01f-234630154e96"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IThumbnailProvider
{
    [PreserveSig]
    int GetThumbnail(uint cx, out IntPtr phbmp, out WTS_ALPHATYPE pdwAlpha);
}

[ComImport, Guid("7f73be3f-fb79-493c-a6c7-7ee14e245841"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IInitializeWithItem
{
    [PreserveSig]
    int Initialize(IShellItem psi, uint grfMode);
}

[ComImport, Guid("00000001-0000-0000-C000-000000000046"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IClassFactory
{
    [PreserveSig] int CreateInstance(IntPtr pUnkOuter, ref Guid riid, out IntPtr ppvObject);
    [PreserveSig] int LockServer([MarshalAs(UnmanagedType.Bool)] bool fLock);
}

[ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellItem
{
    // Order matters — every method must be declared to keep the vtable offsets correct.
    [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
    [PreserveSig] int GetParent(out IntPtr ppsi);
    [PreserveSig] int GetDisplayName(SIGDN sigdnName, out IntPtr ppszName);
    [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
    [PreserveSig] int Compare(IntPtr psi, uint hint, out int piOrder);
}
