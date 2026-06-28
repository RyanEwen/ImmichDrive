# P/Invoke conventions

- Window/tray/icon P/Invoke lives in `ImmichDrive/Classes/NativeMethods.cs`. Cloud Files API
  P/Invoke lives in `ImmichDrive/Classes/CloudFilter/CfApi.cs`. A one-off `SHChangeNotify`
  declaration lives in `ImmichDrive/Services/DriveSecurity.cs`. The thumbnail extension keeps
  its shell-COM P/Invoke in `ImmichDrive.ThumbnailProvider/`.
- Prefer `[LibraryImport]` (source-generated, AOT/trim-safe) for new declarations; use
  `[DllImport]` only where a marshalling pattern isn't yet supported by the generator.
  `DriveSecurity.SHChangeNotify` is the current `[LibraryImport]` example. The bulk of
  `NativeMethods` / `CfApi` is still `[DllImport]` (predates the convention) — don't churn it.
- Group by DLL with a header comment. A class with any `[LibraryImport]` must be `partial`
  (`CfApi` and `DriveSecurity` already are; `NativeMethods` is not, since it's all `[DllImport]`).
- cfapi structs (`CF_PLACEHOLDER_CREATE_INFO`, `CF_CALLBACK_INFO`, `CF_OPERATION_INFO`, …)
  must match `cfapi.h` layout **exactly** — use `[StructLayout(LayoutKind.Sequential)]`, the
  right field order, and verify sizes on-device. A wrong offset silently corrupts hydration
  (a real crash: a wrong `CF_CALLBACK_INFO` field order made `FileIdentity` point at garbage).
- The `CF_CALLBACK_PARAMETERS` / `CF_OPERATION_PARAMETERS` arms are union members: in the
  modeled structs (`CF_CALLBACK_PARAMETERS_FETCHDATA`, `CF_OPERATION_PARAMETERS_TRANSFERDATA`)
  the `uint ParamSize` is followed by 4 bytes of explicit padding (`_pad0`) so the 8-byte-aligned
  union arm starts at offset 8. Omitting that pad was a real crash — keep it on any new arm.
- Tray uses `Shell_NotifyIcon` + `NOTIFYICONDATA` and a `RegisterWindowMessage` callback, on
  the invisible `MainWindow` (see LittleLauncher's pattern). Window icon via `WM_SETICON` +
  `LoadImage(... LR_LOADFROMFILE)`; `AppWindow.SetIcon` for the title bar.
- Load icons at a **generous frame** (32 for the tray/small slot, 64 for big/`AppWindow.SetIcon`),
  not 16, so the shell *downscales* to the DPI-scaled slot (crisp). Loading a 16px frame forces
  an upscale on high-DPI displays → blurry.
- Always `DestroyIcon` HICONs you create; free unmanaged buffers in `finally`.
