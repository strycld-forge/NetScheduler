@echo off
rem Uninstall: remove scheduled tasks and stop the program.
schtasks /delete /f /tn "NetScheduler_AutoStart" >nul 2>&1
schtasks /delete /f /tn "NetScheduler_Watchdog" >nul 2>&1
taskkill /f /im NetScheduler.exe >nul 2>&1
echo NetScheduler uninstalled.
echo Files remain in %LOCALAPPDATA%\NetScheduler - delete that folder manually if desired.
pause
