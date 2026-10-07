$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'EaglePrtSc.exe') --stop
$shortcutPath = Join-Path ([Environment]::GetFolderPath('Startup')) 'EaglePrtSc.lnk'
if (Test-Path -LiteralPath $shortcutPath) {
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    if ($shortcut.TargetPath -ne (Join-Path $PSScriptRoot 'EaglePrtSc.exe')) {
        throw 'The shortcut points to another installation; it was preserved.'
    }
    Remove-Item -LiteralPath $shortcutPath
}
Write-Output 'EaglePrtSc stopped; startup shortcut removed.'
