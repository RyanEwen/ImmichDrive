# Thumbnail shell extension (COM `IThumbnailProvider`)

Project `ImmichDrive.ThumbnailProvider/` builds `ImmichDrive.ThumbnailProvider.exe`
(`OutputType=WinExe`), an **out-of-process** COM server that the shell launches on demand to
render thumbnails for placeholder files **without hydrating** them. It fetches Immich's own
small thumbnail over HTTP.

> **Why an exe, not a DLL?** The first design was an in-process comhost DLL, but it could not
> host the .NET runtime inside the shell's thumbnail surrogate — activation failed with
> `0x80008093`. As a standalone exe the process controls its own runtime and runs with package
> identity.

## Interfaces

- **`IThumbnailProvider`** (`IID_IThumbnailProvider`,
  `e357fccd-a995-4576-b01f-234630154e96`) — `GetThumbnail(cx, out hBitmap, out alpha)`.
- **`IInitializeWithItem`** (`7f73be3f-fb79-493c-a6c7-7ee14e245841`) —
  `Initialize(IShellItem, grfMode)`. Preferred over `IInitializeWithStream` precisely because
  the stream form would read the file and trigger hydration. With the item we get the **path**
  only.

The provider class `ImmichThumbnailProvider` carries a stable CLSID
(`C0A6F3D2-7E41-4B8A-9C5E-2F1A6D3B8E70`, see `ComGuids`). Because it's an exe server, the
process self-registers a class factory at startup rather than exporting `DllGetClassObject`:

- `Program.Main` (STA) calls `CoRegisterClassObject(clsid, ClassFactory, CLSCTX_LOCAL_SERVER,
  REGCLS_MULTIPLEUSE | REGCLS_SUSPENDED)` then `CoResumeClassObjects()`, and pumps a Win32
  message loop.
- `ClassFactory.CreateInstance` hands out `ImmichThumbnailProvider` instances to the shell.
- An idle watchdog `PostThreadMessage(WM_QUIT)`s the loop after ~30 s with no activity (each
  `CreateInstance` / `LockServer` / COM call `Touch()`es the activity timestamp); the shell
  re-launches the exe on the next request.

## Flow

1. `Initialize(IShellItem, _)` → `psi.GetDisplayName(SIGDN_FILESYSPATH)` to get the full path
   **without** opening a stream (no hydration).
2. Reject paths outside the sync root (`SettingsFile.ReadSyncRoot()`) with `E_FAIL` so the
   shell's default handler runs for anything else. For files inside, map path → Immich asset id
   by reading the shared SQLite `AssetIndex` (relativePath ⇄ assetId); the relative path is the
   full path minus the sync-root prefix.
3. `GetThumbnail(cx, …)` → `ImmichClient.GetThumbnailBytesAsync(id, "preview")` (preview = JPEG,
   decodable by GDI+), decode and scale to a 32bpp HBITMAP that fits `cx`, return `WTSAT_RGB`.
   No original is downloaded. The HTTP call blocks briefly with a short timeout (5 s).

The extension reads server URL + API key via `SettingsFile.ReadConnection()` from the same
`settings.json` the app writes, and the index path from `SharedPaths.IndexDbPath` (so it
resolves the same physical DB whether the app is packaged or not). Diagnostics are appended to
`C:\ProgramData\ImmichDrive\thumb.log`, a fixed non-redirected path the surrogate can write.

## Registration (MSIX)

Two manifest pieces, both required:

1. Declare the out-of-process COM server (`windows.comServer` → `com:ComServer` →
   `com:ExeServer Executable="ImmichDrive.ThumbnailProvider.exe"` → `com:Class
   Id="C0A6F3D2-7E41-4B8A-9C5E-2F1A6D3B8E70"`).
2. Associate it with **cloud placeholder files only** via the cloud-files extension — this is
   the correct, **scoped** mechanism (it does NOT override the system thumbnailer for ordinary
   `.jpg`/`.mp4` files elsewhere):

```xml
<desktop3:Extension Category="windows.cloudFiles">
  <desktop3:CloudFiles IconResource="Images\StoreLogo.png">
    <desktop3:CustomStateHandler Clsid="C0A6F3D2-7E41-4B8A-9C5E-2F1A6D3B8E70" />
    <desktop3:ThumbnailProviderHandler Clsid="C0A6F3D2-7E41-4B8A-9C5E-2F1A6D3B8E70" />
    <desktop3:ExtendedPropertyHandler Clsid="C0A6F3D2-7E41-4B8A-9C5E-2F1A6D3B8E70" />
    <desktop3:BannersHandler Clsid="C0A6F3D2-7E41-4B8A-9C5E-2F1A6D3B8E70" />
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
`ThumbnailProviderHandler` fails packaging. The sibling handlers are therefore present too,
all pointing at the same CLSID (`CloudFilesContextMenus` has no `Clsid` attribute and is left
empty). Only `ThumbnailProviderHandler` is actually exercised.

On-device validation points: COM activation of the exe server by the shell, `System.Drawing`
loading there, and the returned HBITMAP format.

## Constraints

- Drives the shell's thumbnail UX: **no WinUI, no NLog, minimal allocations**, fail fast and quiet.
- Linked source (`ImmichAsset`, `ImmichClient`, `AssetIndex`, `SharedPaths`, `SettingsFile`,
  via `<Compile Include="..\ImmichDrive\…" Link="Shared\…" />`) must stay WinUI-free and trim-safe.
- Use a short HTTP timeout (5 s) — a slow server must not hang Explorer.
