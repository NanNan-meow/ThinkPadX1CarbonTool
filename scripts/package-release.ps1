param(
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '1.4.4',
    [string]$ApplicationDirectory = 'dist\CarbonControl-v1.4',
    [string]$OutputDirectory = 'release'
)
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
function Resolve-ProjectPath([string]$Path) {
    if ([IO.Path]::IsPathRooted($Path)) { return [IO.Path]::GetFullPath($Path) }
    return [IO.Path]::GetFullPath((Join-Path $project $Path))
}
$app = Resolve-ProjectPath $ApplicationDirectory
$output = Resolve-ProjectPath $OutputDirectory
$binary = Join-Path $app 'CarbonControl.exe'
if (-not (Test-Path -LiteralPath $binary)) { throw 'Build CarbonControl.exe before packaging.' }
$fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($binary).FileVersion
if ($fileVersion -ne ($Version + '.0')) { throw ('Binary version ' + $fileVersion + ' does not match release ' + $Version) }
New-Item -ItemType Directory -Path $output -Force | Out-Null
$staging = Join-Path $output ('stage-' + $Version + '-' + [Guid]::NewGuid().ToString('N'))
$bundle = Join-Path $staging 'CarbonControl'
New-Item -ItemType Directory -Path $bundle -Force | Out-Null
Copy-Item -LiteralPath $binary -Destination $bundle
Copy-Item -LiteralPath (Join-Path $project 'src\App.config') -Destination (Join-Path $bundle 'CarbonControl.exe.config')
Copy-Item -LiteralPath (Join-Path $project 'scripts\setup-hardware.ps1') -Destination $bundle
Copy-Item -LiteralPath (Join-Path $project 'scripts\test-hardware.ps1') -Destination $bundle
Copy-Item -LiteralPath (Join-Path $project 'README.md'),(Join-Path $project 'THIRD-PARTY-NOTICES.txt') -Destination $bundle
$docs = Join-Path $project 'docs'
if (Test-Path -LiteralPath $docs) { Copy-Item -LiteralPath $docs -Destination $bundle -Recurse }
$notes = Join-Path $project ('docs\RELEASE-v' + $Version + '.md')
if (Test-Path -LiteralPath $notes) { Copy-Item -LiteralPath $notes -Destination (Join-Path $bundle 'RELEASE-NOTES.md') }
@'
@echo off
start "" "%~dp0CarbonControl.exe"
'@ | Set-Content -LiteralPath (Join-Path $bundle 'Start.cmd') -Encoding ASCII
@'
@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0test-hardware.ps1"
pause
'@ | Set-Content -LiteralPath (Join-Path $bundle 'TestHardware.cmd') -Encoding ASCII
@'
@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0test-hardware.ps1" -ReadOnly
pause
'@ | Set-Content -LiteralPath (Join-Path $bundle 'ReadHardware.cmd') -Encoding ASCII
$archive = Join-Path $output ('CarbonControl-v' + $Version + '-win-x64.zip')
Compress-Archive -LiteralPath $bundle -DestinationPath $archive -Force
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
$checksum = Join-Path $output 'SHA256SUMS.txt'
[IO.File]::WriteAllText($checksum, ($hash + '  ' + [IO.Path]::GetFileName($archive) + "`n"), [Text.Encoding]::ASCII)
Write-Host ('Packaged: ' + $archive)
Write-Host ('SHA256: ' + $hash)
