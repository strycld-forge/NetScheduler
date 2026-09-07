' NetScheduler watchdog: start NetScheduler.exe if it is not running.
' Launched by scheduled task "NetScheduler_Watchdog" via wscript.exe, so no console window appears.
Dim fso, wmi, dir, running, shell, proc
Set fso = CreateObject("Scripting.FileSystemObject")
dir = fso.GetParentFolderName(WScript.ScriptFullName)
running = False
Set wmi = GetObject("winmgmts:{impersonationLevel=impersonate}!\\.\root\cimv2")
For Each proc In wmi.InstancesOf("Win32_Process")
    If LCase(proc.Name) = "netscheduler.exe" Then
        running = True
        Exit For
    End If
Next
If Not running Then
    Set shell = CreateObject("WScript.Shell")
    shell.Run """" & dir & "\NetScheduler.exe""", 0, False
End If
