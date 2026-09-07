@echo off
rem Install: copy files to %LOCALAPPDATA%\NetScheduler and register scheduled tasks
rem - NetScheduler_AutoStart : start at logon (highest privilege, no UAC prompt)
rem - NetScheduler_Watchdog  : every 5 minutes via wscript (windowless), restart the exe if it is not running
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
rem Keep the user's existing config; only provide the default template on first install
if not exist "%DEST%\config.ini" copy /y "%~dp0config.ini" "%DEST%\" >nul 2>&1
copy /y "%~dp0watchdog.vbs" "%DEST%\" >nul
copy /y "%~dp0README.md" "%DEST%\" >nul 2>&1

schtasks /create /f /tn "NetScheduler_AutoStart" /sc onlogon /rl highest /tr "\"%DEST%\NetScheduler.exe\""
if errorlevel 1 (
    echo ERROR: Failed to create auto-start task.
    pause
    exit /b 1
)
schtasks /create /f /tn "NetScheduler_Watchdog" /sc minute /mo 5 /rl highest /tr "wscript.exe \"%DEST%\watchdog.vbs\"" >nul
if errorlevel 1 (
    echo WARNING: Failed to create watchdog task. The program still works, but won't auto-restart after a crash.
)

echo Installed to: %DEST%
echo Starting NetScheduler...
start "" "%DEST%\NetScheduler.exe"
echo Done. A tray icon should appear. Edit "%DEST%\config.ini" to configure.
pause
endlocal
