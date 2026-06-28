# P/Invoke conventions

- Window/tray/icon P/Invoke lives in `ImmichDrive/Classes/NativeMethods.cs`. Cloud Files API
  P/Invoke lives in `ImmichDrive/Classes/CloudFilter/CfApi.cs`. The thumbnail extension keeps
  its shell-COM P/Invoke in `ImmichDrive.ThumbnailProvider/`.
- Prefer `[LibraryImport]` (source-generated, AOT/trim-safe) for new declarations; use
  `[DllImport]` only where a marshalling pattern isn't yet supported by the generator.
- Group by DLL with a header comment. Mark `partial` classes for `[LibraryImport]`.
- cfapi structs (`CF_PLACEHOLDER_CREATE_INFO`, `CF_CALLBACK_INFO`, `CF_OPERATION_INFO`, …)
  must match `cfapi.h` layout **exactly** — use `[StructLayout(LayoutKind.Sequential)]`, the
  right field order, and verify sizes on-device. A wrong offset silently corrupts hydration.
- Tray uses `Shell_NotifyIcon` + `NOTIFYICONDATA` and a `RegisterWindowMessage` callback, on
  the invisible `MainWindow` (see LittleLauncher's pattern). Window icon via `WM_SETICON` +
  `LoadImage(... LR_LOADFROMFILE)`; `AppWindow.SetIcon` for the title bar.
- Always `DestroyIcon` HICONs you create; free unmanaged buffers in `finally`.
