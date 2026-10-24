# WDM Release Packaging Script

$ErrorActionPreference = "Stop"
$Version = "1.0.0"
$RootDir = Resolve-Path "$PSScriptRoot/.."
$DistDir = "$RootDir/dist"
$StagingDir = "$DistDir/WDM-v$Version-windows-x64"

Write-Host "=========================================" -ForegroundColor Cyan
Write-Host "   WDM Release Packaging Tool v$Version" -ForegroundColor Cyan
Write-Host "=========================================" -ForegroundColor Cyan

# Clean staging directory
if (Test-Path $DistDir) {
    Remove-Item -Recurse -Force $DistDir
}
New-Item -ItemType Directory -Force -Path $StagingDir | Out-Null

Write-Host "`n[1/4] Building and publishing Desktop UI..." -ForegroundColor Yellow
dotnet publish "$RootDir/src/WDM.UI/WDM.UI.csproj" -c Release -r win-x64 --self-contained false -o "$StagingDir"

Write-Host "`n[2/4] Building and publishing Native Messaging Host..." -ForegroundColor Yellow
dotnet publish "$RootDir/src/WDM.NativeHost/WDM.NativeHost.csproj" -c Release -r win-x64 --self-contained false -o "$StagingDir/NativeHost"

Write-Host "`n[3/4] Copying Browser Extension and Helper Scripts..." -ForegroundColor Yellow
Copy-Item -Recurse "$RootDir/extension" "$StagingDir/extension"
Remove-Item "$StagingDir/NativeHost/*.pdb" -ErrorAction SilentlyContinue
Remove-Item "$StagingDir/*.pdb" -ErrorAction SilentlyContinue
Remove-Item "$StagingDir/*.xml" -ErrorAction SilentlyContinue

# Quick Start Readme for end-users
$QuickStart = @"
============================================================
  WDM (Windows Download Manager) v$Version
============================================================

QUICK START GUIDE:
1. Launch the Desktop App:
   Double-click 'WDM.UI.exe' to open the high-speed download dashboard.

2. Browser Integration (Chrome / Edge / Brave / Firefox):
   a. Double-click 'extension\install-host.bat' to register the Native Host in your Windows Registry.
   b. Open chrome://extensions (or edge://extensions) in your browser.
   c. Turn ON 'Developer mode' (top-right).
   d. Click 'Load unpacked' and select the 'extension' folder inside this directory.

Enjoy maximum download speeds with zero-stitch instant disk completion!
"@
Set-Content -Path "$StagingDir/README.txt" -Value $QuickStart

Write-Host "`n[4/4] Creating Release ZIP Archive..." -ForegroundColor Yellow
$ZipPath = "$DistDir/WDM-v$Version-windows-x64.zip"
Compress-Archive -Path "$StagingDir/*" -DestinationPath $ZipPath -Force

Write-Host "`n[SUCCESS] Release bundle created successfully!" -ForegroundColor Green
Write-Host "Zip Archive: $ZipPath" -ForegroundColor Green
Get-Item $ZipPath | Select-Object Name, Length, LastWriteTime | Format-Table
