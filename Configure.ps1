param([string]$EaglePath, [string]$FFmpegPath, [string]$FFprobePath)
$ErrorActionPreference = 'Stop'
function Resolve-Executable([string]$ExplicitPath, [string]$Name, [string[]]$Candidates) {
    $config = Join-Path $PSScriptRoot ($Name + '-path.txt')
    if ($ExplicitPath) {
        if (-not (Test-Path -LiteralPath $ExplicitPath -PathType Leaf)) { throw ('Executable not found: ' + $ExplicitPath) }
        return (Resolve-Path -LiteralPath $ExplicitPath).Path
    }
    if (Test-Path -LiteralPath $config) {
        $configured = (Get-Content -LiteralPath $config -Raw).Trim()
        if (Test-Path -LiteralPath $configured -PathType Leaf) { return $configured }
    }
    $command = Get-Command ($Name + '.exe') -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    foreach ($candidate in $Candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate -PathType Leaf)) { return $candidate }
    }
    if ($Name -eq 'eagle') { throw ('Cannot find ' + $Name + '. Supply -EaglePath with its absolute executable path.') }
    return $null
}
$eagleCandidates = @((Join-Path $env:ProgramFiles 'Eagle\Eagle.exe'))
if (${env:ProgramFiles(x86)}) { $eagleCandidates += Join-Path ${env:ProgramFiles(x86)} 'Eagle\Eagle.exe' }
$eagle = Resolve-Executable $EaglePath 'eagle' $eagleCandidates
$ffmpeg = Resolve-Executable $FFmpegPath 'ffmpeg' @()
$probeCandidates = @()
if ($ffmpeg) { $probeCandidates += Join-Path ([IO.Path]::GetDirectoryName($ffmpeg)) 'ffprobe.exe' }
$ffprobe = Resolve-Executable $FFprobePath 'ffprobe' $probeCandidates
Set-Content -LiteralPath (Join-Path $PSScriptRoot 'eagle-path.txt') -Value $eagle -Encoding UTF8
if ($ffmpeg) { Set-Content -LiteralPath (Join-Path $PSScriptRoot 'ffmpeg-path.txt') -Value $ffmpeg -Encoding UTF8 }
if ($ffprobe) { Set-Content -LiteralPath (Join-Path $PSScriptRoot 'ffprobe-path.txt') -Value $ffprobe -Encoding UTF8 }
Write-Output 'Configured available paths. Missing FFmpeg/ffprobe will install automatically when you open EaglePrtSc.exe or Start.cmd.'
