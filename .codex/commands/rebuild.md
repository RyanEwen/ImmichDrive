Rebuild and relaunch the app to test a change.

1. Stop any running instance: `Stop-Process -Name ImmichDrive -Force -ErrorAction SilentlyContinue`.
2. Build: `dotnet build ImmichDrive/ImmichDrive.csproj -c Debug` (auto-detects x64/ARM64 from
   `Directory.Build.props`).
3. If it builds, relaunch the produced exe from
   `ImmichDrive/bin/<Platform>/Debug/net10.0-windows10.0.22000.0/ImmichDrive.exe`.
4. Report build errors verbatim; do not "fix" by suppressing warnings.

Note: changes to the thumbnail extension or the cfapi sync-root registration only take full
effect from an installed **MSIX** (the shell loads the registered COM server) — for those,
use `ImmichDriveMSIX/build-msix.ps1` and reinstall.
