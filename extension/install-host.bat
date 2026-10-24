@echo off
setlocal
echo ========================================================
echo   WDM (Windows Download Manager) Native Host Installer
echo ========================================================
echo.

set EXE_PATH=%~dp0..\bin\NativeHost\WDM.NativeHost.exe

if not exist "%EXE_PATH%" (
    echo Building and publishing WDM.NativeHost...
    dotnet publish "%~dp0..\src\WDM.NativeHost\WDM.NativeHost.csproj" -c Release -o "%~dp0..\bin\NativeHost"
)

if exist "%EXE_PATH%" (
    echo Registering Native Messaging Host for Chrome, Edge, and Firefox...
    "%EXE_PATH%" --install
    echo.
    echo [SUCCESS] WDM Native Messaging Host is now registered!
    echo You can now load the 'extension' directory in chrome://extensions or edge://extensions.
) else (
    echo [ERROR] Could not find or build WDM.NativeHost.exe
)

echo.
pause
