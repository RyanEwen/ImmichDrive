<#
.SYNOPSIS
    Builds the ImmichDrive MSIX package (sideload or Store upload).

.DESCRIPTION
    1. Reads the version from Directory.Build.props (single source of truth)
    2. Publishes the WinUI app self-contained (bundles the .NET runtime), WindowsPackageType=MSIX
    3. Publishes the thumbnail shell extension (COM host) and merges it into the layout
    4. Assembles the MSIX layout (app + extension + compiled XAML + stamped manifest + Images)
    5. Generates resources.pri via makepri
    6. Packages with makeappx.exe
    7. Signs with signtool.exe (dev self-signed cert, or a CA-trusted PFX)

    Use -NoSign for Microsoft Store uploads (the Store re-signs during ingestion).
    ASCII only (Windows PowerShell 5.1 reads BOM-less .ps1 as ANSI).

.EXAMPLE
    .\build-msix.ps1
    .\build-msix.ps1 -Platform x64
    .\build-msix.ps1 -NoSign
#>
param(
    [ValidateSet("x64", "ARM64")]
    [string]$Platform = $(if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { "ARM64" } else { "x64" }),

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string]$TrustedPfxPath = "",
    [string]$TrustedPfxPassword = "",
    [switch]$NoSign
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

# -- Paths -------------------------------------------------------------------------
$msixDir     = $PSScriptRoot
$repoRoot    = Split-Path $msixDir -Parent
$mainProj    = Join-Path $repoRoot "ImmichDrive\ImmichDrive.csproj"
$thumbProj   = Join-Path $repoRoot "ImmichDrive.ThumbnailProvider\ImmichDrive.ThumbnailProvider.csproj"
$manifestSrc = Join-Path $msixDir  "Package.appxmanifest"
$imagesDir   = Join-Path $msixDir  "Images"
$pfxFile     = Join-Path $msixDir  "ImmichDrive.pfx"

$rid        = if ($Platform -eq "ARM64") { "win-arm64" } else { "win-x64" }
$tfm        = "net10.0-windows10.0.22000.0"
$publishDir    = Join-Path $repoRoot "ImmichDrive\bin\$Platform\$Configuration\$tfm\$rid\publish"
$thumbPublish  = Join-Path $repoRoot "ImmichDrive.ThumbnailProvider\bin\$Platform\$Configuration\$tfm\$rid\publish"
$layoutDir  = Join-Path $msixDir  "bin\msix-layout\$Platform"
$outputDir  = Join-Path $msixDir  "bin\msix-output"
$msixFile   = Join-Path $outputDir "ImmichDrive-$Platform.msix"

# Packaging tools (makeappx/makepri/signtool) for the native host arch.
$sdkHostArch = if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { "arm64" } else { "x64" }
$buildToolsVersion = "10.0.26100.4654"

function Find-SdkBin([string]$hostArch) {
    $kitsRoot = "C:\Program Files (x86)\Windows Kits\10\bin"
    if (Test-Path $kitsRoot) {
        $hit = Get-ChildItem $kitsRoot -Directory |
            Where-Object { $_.Name -match '^10\.' } |
            Sort-Object { [version]$_.Name } -Descending |
            ForEach-Object { Join-Path $_.FullName $hostArch } |
            Where-Object { Test-Path (Join-Path $_ "makeappx.exe") } |
            Select-Object -First 1
        if ($hit) { return $hit }
    }
    $nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE ".nuget\packages" }
    $btRoot = Join-Path $nugetRoot "microsoft.windows.sdk.buildtools"
    if (Test-Path $btRoot) {
        foreach ($pkg in (Get-ChildItem $btRoot -Directory | Sort-Object { [version]$_.Name } -Descending)) {
            $binRoot = Join-Path $pkg.FullName "bin"
            if (-not (Test-Path $binRoot)) { continue }
            $hit = Get-ChildItem $binRoot -Directory |
                Sort-Object { [version]$_.Name } -Descending |
                ForEach-Object { Join-Path $_.FullName $hostArch } |
                Where-Object { Test-Path (Join-Path $_ "makeappx.exe") } |
                Select-Object -First 1
            if ($hit) { return $hit }
        }
    }
    return $null
}

$sdkBin = Find-SdkBin $sdkHostArch
if (-not $sdkBin) {
    Write-Host "Packaging tools not found - acquiring Microsoft.Windows.SDK.BuildTools via NuGet..." -ForegroundColor Yellow
    $tmp = Join-Path $env:TEMP "imd-sdktools"
    New-Item $tmp -ItemType Directory -Force | Out-Null
    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
  <ItemGroup><PackageReference Include="Microsoft.Windows.SDK.BuildTools" Version="$buildToolsVersion" /></ItemGroup>
</Project>
"@ | Set-Content (Join-Path $tmp "tools.csproj")
    dotnet restore (Join-Path $tmp "tools.csproj") | Out-Null
    $sdkBin = Find-SdkBin $sdkHostArch
}
if (-not $sdkBin) { Write-Error "Could not find or acquire packaging tools (makeappx/makepri/signtool)."; exit 1 }
Write-Host "Using packaging tools: $sdkBin" -ForegroundColor DarkCyan
$makeappx = Join-Path $sdkBin "makeappx.exe"
$signtool = Join-Path $sdkBin "signtool.exe"
$makepri  = Join-Path $sdkBin "makepri.exe"

# -- Version ----------------------------------------------------------------------
$propsXml = [xml](Get-Content (Join-Path $repoRoot "Directory.Build.props"))
$version  = $propsXml.SelectSingleNode("//Version").InnerText
if (-not $version) { Write-Error "Cannot read <Version> from Directory.Build.props"; exit 1 }
$msixVersion = if ($version -match '^\d+\.\d+\.\d+$') { "$version.0" } else { $version }
Write-Host "Version: $msixVersion" -ForegroundColor Cyan

# -- Signing certificate (auto-generate dev cert if missing) ----------------------
if ($NoSign) {
    Write-Host "Skipping signing (Store upload mode)" -ForegroundColor Yellow
} elseif (-not (Test-Path $pfxFile)) {
    Write-Host "Signing cert not found - generating self-signed dev cert..." -ForegroundColor Yellow
    $cert = New-SelfSignedCertificate -Type Custom -Subject "CN=ImmichDrive-Dev" `
        -KeyUsage DigitalSignature -FriendlyName "ImmichDrive Dev" `
        -CertStoreLocation "Cert:\CurrentUser\My" `
        -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3")
    $pwd = ConvertTo-SecureString -String "ImmichDrive" -Force -AsPlainText
    Export-PfxCertificate -Cert $cert -FilePath $pfxFile -Password $pwd | Out-Null
    Export-Certificate -Cert $cert -FilePath (Join-Path $msixDir "ImmichDrive.cer") | Out-Null
    Write-Host "  Created $pfxFile"
    Write-Host "  Trust it (admin) before installing the MSIX:" -ForegroundColor Cyan
    Write-Host "    Import-Certificate -FilePath '$($msixDir)\ImmichDrive.cer' -CertStoreLocation Cert:\LocalMachine\TrustedPeople" -ForegroundColor DarkCyan
}

# -- Step 1: Publish app (self-contained) -----------------------------------------
Write-Host "`n=== Publishing ImmichDrive ($Platform $Configuration) ===" -ForegroundColor Cyan
dotnet publish $mainProj -c $Configuration -r $rid -p:Platform=$Platform `
    --self-contained -p:PublishSingleFile=false -p:WindowsPackageType=MSIX
if ($LASTEXITCODE -ne 0) { Write-Error "dotnet publish (app) failed"; exit 1 }

# -- Step 2: Publish thumbnail extension (COM host) --------------------------------
# Self-contained so the comhost resolves the .NET runtime from its own folder when the shell
# activates it in a surrogate (a framework-dependent comhost can't find a shared framework
# inside the package and silently fails to activate -> no thumbnails for online-only files).
Write-Host "`n=== Publishing ImmichDrive.ThumbnailProvider (COM extension) ===" -ForegroundColor Cyan
dotnet publish $thumbProj -c $Configuration -r $rid -p:Platform=$Platform --self-contained true
if ($LASTEXITCODE -ne 0) { Write-Error "dotnet publish (extension) failed"; exit 1 }

# -- Step 3: Assemble layout ------------------------------------------------------
Write-Host "`n=== Assembling MSIX layout ===" -ForegroundColor Cyan
if (Test-Path $layoutDir) { Remove-Item $layoutDir -Recurse -Force }
New-Item $layoutDir -ItemType Directory -Force | Out-Null

Copy-Item "$publishDir\*" $layoutDir -Recurse -Force

# Merge the extension's published files (dll + comhost.dll + Sqlite/Drawing deps).
Copy-Item "$thumbPublish\*" $layoutDir -Recurse -Force

# dotnet publish omits compiled XAML (.xbf) - copy from the RID build dir.
$ridBuildDir = Split-Path $publishDir -Parent
Get-ChildItem $ridBuildDir -Filter "*.xbf" -Recurse |
    Where-Object { $_.FullName -notlike "*\publish\*" } |
    ForEach-Object {
        $rel = $_.FullName.Substring($ridBuildDir.Length + 1)
        $dest = Join-Path $layoutDir $rel
        $destDir = Split-Path $dest -Parent
        if (-not (Test-Path $destDir)) { New-Item $destDir -ItemType Directory -Force | Out-Null }
        Copy-Item $_.FullName $dest -Force
    }

# Stamp manifest placeholders.
$msixArch = if ($Platform -eq "ARM64") { "arm64" } else { "x64" }
$manifestContent = (Get-Content $manifestSrc -Raw) `
    -replace 'ARCH_PLACEHOLDER', $msixArch `
    -replace 'VERSION_PLACEHOLDER', $msixVersion

if (-not $NoSign) {
    $signingPfx = if ([string]::IsNullOrEmpty($TrustedPfxPath)) { $pfxFile } else { $TrustedPfxPath }
    $signingPwd = if ([string]::IsNullOrEmpty($TrustedPfxPath)) { "ImmichDrive" } else { $TrustedPfxPassword }
    $signCert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($signingPfx, $signingPwd)
    if ($manifestContent -match '<Identity[^>]+Publisher="([^"]+)"') {
        $manifestContent = $manifestContent.Replace("Publisher=`"$($Matches[1])`"", "Publisher=`"$($signCert.Subject)`"")
        Write-Host "  Stamped Publisher=$($signCert.Subject)"
    }
    # Use the dev identity Name for sideload builds so they form a stable package family that updates
    # in place. The real Store Name (from Partner Center) is only used for -NoSign (Store) builds.
    if ($manifestContent -match '<Identity[^>]+Name="([^"]+)"') {
        $manifestContent = $manifestContent.Replace("Name=`"$($Matches[1])`"", "Name=`"ImmichDrive`"")
        Write-Host "  Stamped dev Name=ImmichDrive"
    }
}

Set-Content -Path (Join-Path $layoutDir "AppxManifest.xml") -Value $manifestContent -NoNewline
Copy-Item $imagesDir (Join-Path $layoutDir "Images") -Recurse -Force
Write-Host "  Layout ready: $layoutDir"

# -- Step 4: resources.pri --------------------------------------------------------
Write-Host "`n=== Generating resources.pri ===" -ForegroundColor Cyan
$existingPri = Join-Path $layoutDir "resources.pri"
if (Test-Path $existingPri) { Remove-Item $existingPri -Force }
$priconfigFile = Join-Path $layoutDir "priconfig.xml"
& $makepri createconfig /cf $priconfigFile /dq en-US /o
if ($LASTEXITCODE -ne 0) { Write-Error "makepri createconfig failed"; exit 1 }
& $makepri new /pr $layoutDir /cf $priconfigFile /mn (Join-Path $layoutDir "AppxManifest.xml") /of $existingPri /o
if ($LASTEXITCODE -ne 0) { Write-Error "makepri new failed"; exit 1 }
Remove-Item $priconfigFile -Force -ErrorAction SilentlyContinue

# -- Step 5: Package --------------------------------------------------------------
Write-Host "`n=== Packaging MSIX ===" -ForegroundColor Cyan
if (-not (Test-Path $outputDir)) { New-Item $outputDir -ItemType Directory -Force | Out-Null }
if (Test-Path $msixFile) { Remove-Item $msixFile -Force }
& $makeappx pack /d $layoutDir /p $msixFile /o
if ($LASTEXITCODE -ne 0) { Write-Error "makeappx pack failed"; exit 1 }

# -- Step 6: Sign -----------------------------------------------------------------
if ($NoSign) {
    Write-Host "`n=== Skipping MSIX signing (Store upload) ===" -ForegroundColor Yellow
} else {
    Write-Host "`n=== Signing MSIX ===" -ForegroundColor Cyan
    if (-not [string]::IsNullOrEmpty($TrustedPfxPath)) {
        & $signtool sign /fd SHA256 /f $TrustedPfxPath /p $TrustedPfxPassword /tr http://timestamp.digicert.com /td SHA256 $msixFile
    } else {
        & $signtool sign /fd SHA256 /a /f $pfxFile /p "ImmichDrive" $msixFile
    }
    if ($LASTEXITCODE -ne 0) { Write-Error "signtool sign failed"; exit 1 }
}

$size = [math]::Round((Get-Item $msixFile).Length / 1MB, 1)
Write-Host "`n=== SUCCESS ===" -ForegroundColor Green
Write-Host "  MSIX:     $msixFile ($size MB)"
Write-Host "  Version:  $msixVersion  Platform: $Platform"
Write-Host "  Install:  Add-AppxPackage -Path '$msixFile' -ForceUpdateFromAnyVersion"
Write-Host "  Then open ImmichDrive and enter your Immich server URL + API key."
