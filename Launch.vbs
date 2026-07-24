' Starts the Dev Launcher with no console window flashing on screen.
Dim shell
Set shell = CreateObject("WScript.Shell")
shell.Run "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File ""C:\Dev\launcher\AppLauncher.ps1""", 0, False
