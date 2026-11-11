param([string]$ApplicationDirectory, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
if (-not $ApplicationDirectory) {
    $ApplicationDirectory = $PSScriptRoot
    if (-not (Test-Path -LiteralPath (Join-Path $ApplicationDirectory 'CarbonControl.exe'))) {
        $ApplicationDirectory = Join-Path (Split-Path -Parent $PSScriptRoot) 'dist\CarbonControl-v1.4'
    }
}
$binary = Join-Path $ApplicationDirectory 'CarbonControl.exe'
if (-not (Test-Path -LiteralPath $binary)) { throw 'CarbonControl.exe was not found.' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $ApplicationDirectory 'reports' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$report = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) ('physical-test-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.txt')
$process = Start-Process -FilePath $binary -ArgumentList @('--physical-test', ('"' + $report + '"')) -WindowStyle Hidden -Wait -PassThru
Get-Content -LiteralPath $report -Encoding UTF8
Write-Host ("Report: " + $report)
exit $process.ExitCode
