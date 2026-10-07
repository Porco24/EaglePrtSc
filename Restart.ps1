$ErrorActionPreference = 'Stop'
function Install-PendingUpdate([string]$installationDirectory) {
    $pendingUpdate = Join-Path $installationDirectory 'EaglePrtSc.update'
    if (-not (Test-Path -LiteralPath $pendingUpdate)) { return }
    $updateInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($pendingUpdate)
    if ($updateInfo.ProductName -ne 'EaglePrtSc') { throw 'The pending update is not an EaglePrtSc executable.' }
    $targetExecutable = Join-Path $installationDirectory 'EaglePrtSc.exe'
    $expectedHash = (Get-FileHash -LiteralPath $pendingUpdate -Algorithm SHA256).Hash
    $retired = Join-Path $installationDirectory 'EaglePrtSc.retired'
    if (Test-Path -LiteralPath $retired) {
        $retired = Join-Path $installationDirectory ('EaglePrtSc-' + [DateTime]::UtcNow.ToString('yyyyMMddHHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,6) + '.retired')
    }
    # The old process can release its mutex shortly before Windows releases the image.
    $replaced = $false
    for ($attempt = 0; $attempt -lt 50; $attempt++) {
        try {
            [IO.File]::Replace($pendingUpdate, $targetExecutable, $retired, $true)
            $replaced = $true
            break
        } catch [IO.IOException] { Start-Sleep -Milliseconds 100 }
    }
    if (-not $replaced) { throw 'Could not install the pending update. Exit EaglePrtSc, then run Start.cmd again.' }
    if ((Get-FileHash -LiteralPath $targetExecutable -Algorithm SHA256).Hash -ne $expectedHash) { throw 'Update validation failed.' }
}
$executable = Join-Path $PSScriptRoot 'EaglePrtSc.exe'
Start-Process -FilePath $executable -ArgumentList '--stop' -WindowStyle Hidden -Wait
$mutexName = 'Local\EaglePrtSc-' + [Environment]::UserName
$mutex = New-Object Threading.Mutex($false, $mutexName)
$acquired = $false
try {
    try { $acquired = $mutex.WaitOne(60000) }
    catch [Threading.AbandonedMutexException] { $acquired = $true }
    if (-not $acquired) { throw 'Please exit EaglePrtSc from its tray menu, then run Start.cmd again.' }
    Install-PendingUpdate $PSScriptRoot
} finally {
    if ($acquired) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
Start-Process -FilePath $executable -WindowStyle Hidden
