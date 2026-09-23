<#
.SYNOPSIS
Builds one Store upload containing the x64 and ARM64 MSIX packages.
.DESCRIPTION
Partner Center treats a ZIP of loose MSIX files as one x64 package. MakeAppx
creates a real MSIX bundle, which the Store can distribute by architecture.
#>
param(
    [Parameter(Mandatory)][string]$AppName,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version
)

$ErrorActionPreference = 'Stop'
$packages = @('x64', 'ARM64') | ForEach-Object {
    $path = Join-Path $OutputDirectory "$AppName-$Version-$_.msix"
    if (!(Test-Path -LiteralPath $path)) { throw "Missing Store package: $path" }
    $path
}

$sdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
$makeappx = Get-ChildItem $sdkRoot -Directory |
    Where-Object { $_.Name -match '^10\.' } |
    Sort-Object { [version]$_.Name } -Descending |
    ForEach-Object { Join-Path $_.FullName 'x64\makeappx.exe' } |
    Where-Object { Test-Path -LiteralPath $_ } |
    Select-Object -First 1
if (!$makeappx) { throw 'Windows SDK MakeAppx.exe is unavailable.' }

$bundleInput = Join-Path $OutputDirectory 'store-bundle-input'
New-Item $bundleInput -ItemType Directory -Force | Out-Null
Get-ChildItem $bundleInput -File | Remove-Item -Force
foreach ($package in $packages) {
    Copy-Item -LiteralPath $package -Destination $bundleInput
}

$bundlePath = Join-Path $OutputDirectory "$AppName-$Version.msixbundle"
if (Test-Path $bundlePath) { Remove-Item $bundlePath -Force }
& $makeappx bundle /d $bundleInput /p $bundlePath /bv "$Version.0" /o
if ($LASTEXITCODE -ne 0) { throw "MakeAppx bundle failed (exit $LASTEXITCODE)" }

Add-Type -AssemblyName System.IO.Compression.FileSystem
$bundle = [IO.Compression.ZipFile]::OpenRead($bundlePath)
try {
    $manifest = $bundle.GetEntry('AppxMetadata/AppxBundleManifest.xml')
    if (!$manifest) { throw 'The MSIX bundle has no bundle manifest.' }
    $reader = [IO.StreamReader]::new($manifest.Open())
    try { [xml]$xml = $reader.ReadToEnd() }
    finally { $reader.Dispose() }
    $bundleVersion = $xml.SelectSingleNode("//*[local-name()='Bundle']/*[local-name()='Identity']").Version
    if ($bundleVersion -ne "$Version.0") {
        throw "Unexpected MSIX bundle version: $bundleVersion"
    }
    $architectures = @($xml.SelectNodes("//*[local-name()='Package']") | ForEach-Object { $_.Architecture })
    foreach ($architecture in @('x64', 'arm64')) {
        if ($architectures -notcontains $architecture) {
            throw "The MSIX bundle is missing $architecture. Found: $($architectures -join ', ')"
        }
    }
} finally {
    $bundle.Dispose()
}

$uploadPath = Join-Path $OutputDirectory "$AppName-$Version.msixupload"
if (Test-Path $uploadPath) { Remove-Item $uploadPath -Force }
$upload = [IO.Compression.ZipFile]::Open($uploadPath, [IO.Compression.ZipArchiveMode]::Create)
try {
    [IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
        $upload, $bundlePath, [IO.Path]::GetFileName($bundlePath)
    ) | Out-Null
} finally {
    $upload.Dispose()
}

Write-Output "Store upload: $uploadPath; bundled architectures: $($architectures -join ', ')"
