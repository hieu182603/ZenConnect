Option Explicit

Dim shell, fileSystem, scriptDirectory, runScript, command
Set shell = CreateObject("WScript.Shell")
Set fileSystem = CreateObject("Scripting.FileSystemObject")

scriptDirectory = fileSystem.GetParentFolderName(WScript.ScriptFullName)
runScript = fileSystem.BuildPath(scriptDirectory, "run.ps1")
command = "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File """ & runScript & """"

' Window style 0 keeps the PowerShell bootstrap and server console completely hidden.
shell.Run command, 0, False
