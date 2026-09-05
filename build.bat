@echo off
rem Build NetScheduler.exe with the C# compiler bundled in Windows (.NET Framework 4.x).
setlocal
set CSC=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%SystemRoot%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
    echo ERROR: .NET Framework 4.x C# compiler not found.
    exit /b 1
)

"%CSC%" /nologo /codepage:65001 /target:winexe /out:"%~dp0NetScheduler.exe" /win32manifest:"%~dp0app.manifest" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Management.dll /r:System.Xml.dll "%~dp0src\*.cs"
if errorlevel 1 (
    echo BUILD FAILED
    exit /b 1
)
echo BUILD OK: %~dp0NetScheduler.exe
endlocal
