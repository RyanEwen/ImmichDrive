# Thumbnail shell extension (COM `IThumbnailProvider`)

Project `ImmichDrive.ThumbnailProvider/` builds a small in-process COM DLL that Explorer
loads (inside `dllhost.exe` / the shell) to render thumbnails for placeholder files **without
hydrating** them. It fetches Immich's own small thumbnail over HTTP.

## Interfaces

- **`IThumbnailProvider`** (`IID_IThumbnailProvider`,
  `e357fccd-a995-4576-b01f-234630154e96`) — `GetThumbnail(cx, out hBitmap, out alpha)`.
- **`IInitializeWithItem`** (`7f73be3f-fb79-493c-a6c7-7ee14e245841`) —
  `Initialize(IShellItem, grfMode)`. Preferred over `IInitializeWithStream` precisely because
  the stream form would read the file and trigger hydration. With the item we get the **path**
  only.

The class is a COM server with a stable CLSID (see `ComGuids`), exported via the standard
`DllGetClassObject` / `DllCanUnloadNow` (a `[UnmanagedCallersOnly]` export + class factory).

## Flow

1. `Initialize(IShellItem, _)` → `psi.GetDisplayName(SIGDN_FILESYSPATH)` to get the full path
   **without** opening a stream (no hydration).
2. Map path → Immich asset id:
   - primary: read the shared SQLite `AssetIndex` (relativePath ⇄ assetId). The relative path
     is the file path minus the sync-root prefix.
   - fallback: open the placeholder with `CreateFile(FILE_FLAG_OPEN_NO_RECALL |
     FILE_FLAG_BACKUP_SEMANTICS)` and `CfGetPlaceholderInfo` to read the `FileIdentity` blob.
3. `GetThumbnail(cx, …)` → `ImmichClient.GetThumbnailBytesAsync(id, size)` (`size=preview` for
   larger requests, `thumbnail` for small), decode to a 32bpp HBITMAP scaled to fit `cx`,
   return `WTSAT_ARGB`. No original is downloaded.

The extension reads server URL + API key from the same `settings.json` the app writes, and
the index path from the `%LocalAppData%\ImmichDrive` breadcrumb (so it works whether the app
is packaged or not).

## Registration (MSIX)

Two manifest pieces, both required:

1. Declare the in-process COM server (`windows.comServer` → `com:SurrogateServer` →
   `com:Class` pointing at `ImmichDrive.ThumbnailProvider.comhost.dll`, `ThreadingModel="Both"`).
2. Associate it with **cloud placeholder files only** via the cloud-files extension — this is
   the correct, **scoped** mechanism (it does NOT override the system thumbnailer for ordinary
   `.jpg`/`.mp4` files elsewhere):

```xml
<desktop3:Extension Category="windows.cloudFiles">
  <desktop3:CloudFiles IconResource="Images\StoreLogo.png">
    <desktop3:CustomStateHandler />
    <desktop3:ThumbnailProviderHandler Clsid="{CLSID}" />
    <desktop3:ExtendedPropertyHandler />
    <desktop3:BannersHandler />
    <desktop3:CloudFilesContextMenus />
  </desktop3:CloudFiles>
</desktop3:Extension>
```

`desktop3:CloudFiles` (namespace `…/desktop/windows10/3`) registers shell handlers that apply
only to our sync-root placeholders — the shell calls our `IThumbnailProvider` for online-only
files in the drive and nothing else.

**Schema gotcha:** `CT_CloudFiles` is an `xs:all` where `CustomStateHandler`,
`ThumbnailProviderHandler`, `ExtendedPropertyHandler`, `BannersHandler`, and
`CloudFilesContextMenus` all default to `minOccurs="1"` (required), so a lone
`ThumbnailProviderHandler` fails packaging. But each handler's `Clsid` attribute is **optional**,
so the handlers we don't implement are included **empty** (no `Clsid`) — the shell treats them as
absent. Only `ThumbnailProviderHandler` gets a real CLSID.

On-device validation points: COM activation of the comhost in the shell surrogate, `System.Drawing`
loading there, and the returned HBITMAP format.

## Constraints

- Runs inside the shell: **no WinUI, no NLog, minimal allocations**, fail fast and quiet.
- Linked source (`ImmichClient`, `AssetIndex` reader) must stay trim-safe.
- Use a short HTTP timeout (a couple seconds) — a slow server must not hang Explorer.
