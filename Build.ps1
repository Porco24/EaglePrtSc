param([switch]$Validate, [switch]$ValidateMedia)
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework x64 C# compiler was not found.' }
$buildDirectory = Join-Path $PSScriptRoot '.build'
New-Item -ItemType Directory -Path $buildDirectory -Force | Out-Null
$compiled = Join-Path $buildDirectory 'EaglePrtSc.exe'
& $compiler /nologo /target:winexe /platform:x64 /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll ('/out:' + $compiled) (Join-Path $PSScriptRoot 'EaglePrtSc.cs') (Join-Path $PSScriptRoot 'Recording.cs') (Join-Path $PSScriptRoot 'Settings.cs') (Join-Path $PSScriptRoot 'Dependencies.cs')
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
if ($Validate) {
    $dependencyDirectory = Join-Path $buildDirectory ('dependencies-' + [Guid]::NewGuid().ToString('N'))
    $process = Start-Process -FilePath $compiled -ArgumentList @('--test-dependencies', ('"' + $dependencyDirectory + '"')) -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw ('Dependency installer validation failed; see ' + $dependencyDirectory + '\failure.txt') }
    Write-Output (Get-Content -LiteralPath (Join-Path $dependencyDirectory 'dependency-test.txt'))
    foreach ($argument in @('--test-state', '--test-queue')) {
        $process = Start-Process -FilePath $compiled -ArgumentList $argument -WindowStyle Hidden -Wait -PassThru
        if ($process.ExitCode -ne 0) { throw ('Check failed: ' + $argument + ', exit ' + $process.ExitCode) }
        Write-Output ('PASS ' + $argument)
    }
    $cleanupDirectory = Join-Path $buildDirectory ('cleanup-' + [Guid]::NewGuid().ToString('N'))
    $process = Start-Process -FilePath $compiled -ArgumentList @('--test-cache-cleanup', ('"' + $cleanupDirectory + '"')) -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw ('Cache cleanup validation failed; see ' + $cleanupDirectory + '\failure.txt') }
    Write-Output 'PASS verified cache cleanup and unavailable/mismatched destination retention'
    $importDirectory = Join-Path $buildDirectory ('imports-' + [Guid]::NewGuid().ToString('N'))
    $process = Start-Process -FilePath $compiled -ArgumentList @('--test-import-pipeline', ('"' + $importDirectory + '"')) -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw ('Import pipeline validation failed; see ' + $importDirectory + '\failure.txt') }
    Write-Output (Get-Content -LiteralPath (Join-Path $importDirectory 'import-pipeline-test.txt'))
    $startupDirectory = Join-Path $buildDirectory ('startup-' + [Guid]::NewGuid().ToString('N'))
    $process = Start-Process -FilePath $compiled -ArgumentList @('--test-startup', ('"' + $startupDirectory + '"')) -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw ('Startup validation failed; see ' + $startupDirectory + '\failure.txt') }
    Write-Output 'PASS Windows startup shortcut creation/removal in isolated directory'
    $bitrateDirectory = Join-Path $buildDirectory ('bitrate-' + [Guid]::NewGuid().ToString('N'))
    $process = Start-Process -FilePath $compiled -ArgumentList @('--test-bitrate', ('"' + $bitrateDirectory + '"')) -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw ('Bitrate/default validation failed; see ' + $bitrateDirectory + '\failure.txt') }
    Write-Output 'PASS 60fps defaults, bitrate recommendations and manual bitrate preservation'
}
if ($ValidateMedia) {
    foreach ($name in @('ffmpeg-path.txt','ffprobe-path.txt')) {
        $path = Join-Path $PSScriptRoot $name
        if (Test-Path -LiteralPath $path) { Copy-Item -LiteralPath $path -Destination $buildDirectory -Force }
    }
    $audioDirectory = Join-Path $buildDirectory ('audio-timeline-' + [Guid]::NewGuid().ToString('N'))
    $process = Start-Process -FilePath $compiled -ArgumentList @('--test-audio-timeline', ('"' + $audioDirectory + '"')) -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw ('Audio timeline validation failed; see ' + $audioDirectory + '\failure.txt') }
    Write-Output (Get-Content -LiteralPath (Join-Path $audioDirectory 'audio-timeline-test.txt'))
    $validationDirectory = Join-Path $buildDirectory ('video-validation-' + [Guid]::NewGuid().ToString('N'))
    $process = Start-Process -FilePath $compiled -ArgumentList @('--test-video-validation', ('"' + $validationDirectory + '"')) -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw ('Upload video validation failed; see ' + $validationDirectory + '\failure.txt') }
    Write-Output (Get-Content -LiteralPath (Join-Path $validationDirectory 'video-validation-test.txt'))
    $testDirectory = Join-Path $buildDirectory ('media-' + [Guid]::NewGuid().ToString('N'))
    $process = Start-Process -FilePath $compiled -ArgumentList @('--test-settings', ('"' + $testDirectory + '"')) -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw ('Media validation failed; see ' + $testDirectory + '\failure.txt') }
    Write-Output 'PASS settings, image encoding, video scaling/frame rate, silent/system audio and encoded volume'
    $colorDirectory = Join-Path $buildDirectory ('color-' + [Guid]::NewGuid().ToString('N'))
    $process = Start-Process -FilePath $compiled -ArgumentList @('--test-color', ('"' + $colorDirectory + '"')) -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw ('Color validation failed; see ' + $colorDirectory + '\failure.txt') }
    Write-Output (Get-Content -LiteralPath (Join-Path $colorDirectory 'color-test.txt'))
}
Copy-Item -LiteralPath $compiled -Destination (Join-Path $PSScriptRoot 'EaglePrtSc.exe') -Force
Write-Output 'Built EaglePrtSc.exe. Run Configure.ps1 to set installation paths, then Start.cmd from Explorer.'
