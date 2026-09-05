@echo off
rem Uninstall: remove the auto-start scheduled task and stop the program.
schtasks /delete /f /tn "NetScheduler_AutoStart" >nul 2>&1
taskkill /f /im NetScheduler.exe >nul 2>&1
echo NetScheduler uninstalled.
echo Files remain in %LOCALAPPDATA%\NetScheduler - delete that folder manually if desired.
pause
