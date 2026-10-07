$ErrorActionPreference = 'Stop'
$executable = Join-Path $PSScriptRoot 'EaglePrtSc.exe'
$shortcutPath = Join-Path ([Environment]::GetFolderPath('Startup')) 'EaglePrtSc.lnk'
if (-not (Test-Path -LiteralPath $executable)) { throw 'EaglePrtSc.exe is missing.' }
$shell = New-Object -ComObject WScript.Shell
$localShortcutPath = Join-Path $PSScriptRoot 'EaglePrtSc.lnk'
$shortcut = $shell.CreateShortcut($localShortcutPath)
$shortcut.TargetPath = $executable
$shortcut.Arguments = '--background'
$shortcut.WorkingDirectory = $PSScriptRoot
$shortcut.Description = 'Double-press PrtSc to save a native full-screen screenshot to Eagle'
$shortcut.Save()
Copy-Item -LiteralPath $localShortcutPath -Destination $shortcutPath -Force
Write-Output "Startup enabled: $shortcutPath"
