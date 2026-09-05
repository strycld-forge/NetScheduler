@echo off
rem Install: copy files to %LOCALAPPDATA%\NetScheduler and register an auto-start
rem scheduled task (highest privilege, no UAC prompt at every logon).
setlocal

net session >nul 2>&1
if errorlevel 1 (
    echo ERROR: Please right-click install.bat and choose "Run as administrator".
    pause
    exit /b 1
)

if not exist "%~dp0NetScheduler.exe" (
    echo ERROR: NetScheduler.exe not found. Run build.bat first.
    pause
    exit /b 1
)

set DEST=%LOCALAPPDATA%\NetScheduler
if not exist "%DEST%" mkdir "%DEST%"
copy /y "%~dp0NetScheduler.exe" "%DEST%\" >nul
copy /y "%~dp0config.ini" "%DEST%\" >nul 2>&1
copy /y "%~dp0README.md" "%DEST%\" >nul 2>&1

schtasks /create /f /tn "NetScheduler_AutoStart" /sc onlogon /rl highest /tr "\"%DEST%\NetScheduler.exe\""
if errorlevel 1 (
    echo ERROR: Failed to create scheduled task.
    pause
    exit /b 1
)

echo Installed to: %DEST%
echo Starting NetScheduler...
start "" "%DEST%\NetScheduler.exe"
echo Done. A tray icon should appear. Edit "%DEST%\config.ini" to configure.
pause
endlocal
