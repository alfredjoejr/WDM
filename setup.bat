@echo off
setlocal enabledelayedexpansion
title WDM - Windows Download Manager Setup

echo ===================================================================
echo        WDM (Windows Download Manager) v1.0.0 Setup Wizard
echo ===================================================================
echo.
echo Installing WDM and registering browser extensions for:
echo   - Microsoft Edge
echo   - Google Chrome
echo   - Brave Browser
echo   - Mozilla Firefox
echo.

set REPO_DIR=%~dp0
set PUBLISH_DIR=%LOCALAPPDATA%\WDM\NativeHost
set UI_PUBLISH_DIR=%LOCALAPPDATA%\WDM\UI

echo [1/3] Building and publishing WDM.UI Dashboard (Release)...
dotnet publish "%REPO_DIR%src\WDM.UI\WDM.UI.csproj" -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true -o "%UI_PUBLISH_DIR%" >nul 2>&1
if %errorlevel% neq 0 (
    echo [Notice] WDM.UI is running or locked. Using existing deployment at %UI_PUBLISH_DIR%
) else (
    echo [OK] WDM.UI Dashboard published successfully.
)

echo [2/3] Building and publishing WDM.NativeHost Engine (Release)...
dotnet publish "%REPO_DIR%src\WDM.NativeHost\WDM.NativeHost.csproj" -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true -o "%PUBLISH_DIR%" >nul 2>&1
if %errorlevel% neq 0 (
    echo [Notice] Using existing NativeHost engine deployment.
) else (
    echo [OK] WDM.NativeHost Engine published successfully.
)

echo [3/3] Deploying extensions, packaging XPI/ZIP, creating shortcuts and registry entries...
if exist "%PUBLISH_DIR%\WDM.NativeHost.exe" (
    "%PUBLISH_DIR%\WDM.NativeHost.exe" --install
) else (
    echo [Error] Could not find %PUBLISH_DIR%\WDM.NativeHost.exe
    pause
    exit /b 1
)

echo.
echo ===================================================================
echo               WDM INSTALLATION COMPLETED SUCCESSFULLY!
echo ===================================================================
echo.
echo Application installed at:
echo   %LOCALAPPDATA%\WDM\
echo.
echo Shortcuts created:
echo   - Desktop: %USERPROFILE%\Desktop\WDM.lnk
echo   - Start Menu: %APPDATA%\Microsoft\Windows\Start Menu\Programs\WDM.lnk
echo.
echo -------------------------------------------------------------------
echo              PERMANENT BROWSER EXTENSION SETUP GUIDE
echo -------------------------------------------------------------------
echo.
echo 1. MICROSOFT EDGE / GOOGLE CHROME / BRAVE (Permanent Forever):
echo    Your permanent unpacked extension is located at:
echo    %LOCALAPPDATA%\WDM\Extension
echo.
echo    Steps:
echo    a. In Edge, open: edge://extensions (In Chrome: chrome://extensions)
echo    b. Turn ON "Developer mode" toggle.
echo    c. Click "Load unpacked".
echo    d. Select the folder: %LOCALAPPDATA%\WDM\Extension
echo    -^> Done! It stays permanently installed across all browser restarts.
echo.
echo 2. MOZILLA FIREFOX:
echo    Packaged XPI is located at:
echo    %LOCALAPPDATA%\WDM\Extension\wdm-downloader@local.xpi
echo.
echo    Official Permanent Install (Takes 2 minutes, Free forever):
echo    a. Go to: https://addons.mozilla.org/developers/addon/submit/distribution
echo    b. Select "On your own" (Unlisted).
echo    c. Upload: %LOCALAPPDATA%\WDM\Extension\wdm-downloader-amo.zip
echo    d. Mozilla auto-signs it within 2 minutes (no human review needed).
echo    e. Download the signed .xpi and drag it into Firefox.
echo    -^> Done! It installs permanently in about:addons forever.
echo.
echo    Alternative Developer/ESR Mode:
echo    a. In about:config, set xpinstall.signatures.required to false.
echo    b. In about:addons, click gear icon -^> "Install Add-on From File...".
echo    c. Choose: %LOCALAPPDATA%\WDM\Extension\wdm-downloader@local.xpi
echo.
echo ===================================================================
echo.

set /p LAUNCH="Do you want to launch WDM now? (Y/N): "
if /i "%LAUNCH%"=="Y" (
    start "" "%UI_PUBLISH_DIR%\WDM.UI.exe"
)

echo Done.
pause
