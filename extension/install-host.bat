@echo off
setlocal
echo ========================================================
echo   WDM (Windows Download Manager) Native Host Installer
echo ========================================================
echo.

set EXE_PATH=%~dp0..\bin\NativeHost\WDM.NativeHost.exe

echo [1/2] Building and publishing latest WDM.UI Dashboard...
dotnet publish "%~dp0..\src\WDM.UI\WDM.UI.csproj" -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true -o "%~dp0..\bin\WDM.UI"

echo [2/2] Building and publishing latest WDM.NativeHost...
dotnet publish "%~dp0..\src\WDM.NativeHost\WDM.NativeHost.csproj" -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true -o "%~dp0..\bin\NativeHost"

if exist "%EXE_PATH%" (
    echo Registering Native Messaging Host for Chrome, Edge, Brave, and Firefox...
    "%EXE_PATH%" --install
    echo.
    echo [SUCCESS] WDM Native Messaging Host and Dashboard are now registered!
    echo You can now load or reload the 'extension' directory in chrome://extensions or edge://extensions.
) else (
    echo [ERROR] Could not find or build WDM.NativeHost.exe
)

echo.
pause
