param([switch]$Test, [switch]$Preview, [string]$OutputDirectory = 'dist\CarbonControl-v1.4')
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$framework = Split-Path -Parent $compiler
$wpf = Join-Path $framework 'WPF'
$out = Join-Path $PSScriptRoot $OutputDirectory
New-Item -ItemType Directory -Path $out -Force | Out-Null
$previousApp = Join-Path $PSScriptRoot 'dist\CarbonControl'
foreach ($candidate in @('dist\CarbonControl-v1.3','dist\CarbonControl-v1.2')) {
    if (-not (Test-Path -LiteralPath (Join-Path $previousApp 'hardware'))) { $previousApp = Join-Path $PSScriptRoot $candidate }
}
if (-not (Test-Path -LiteralPath (Join-Path $out 'hardware')) -and (Test-Path -LiteralPath (Join-Path $previousApp 'hardware'))) {
    Copy-Item -LiteralPath (Join-Path $previousApp 'hardware') -Destination $out -Recurse -Force
}
$references = @('System.dll','System.Core.dll','System.Xml.dll','System.Drawing.dll','System.Windows.Forms.dll','Microsoft.CSharp.dll') | ForEach-Object { '/reference:' + (Join-Path $framework $_) }
$references += @('PresentationFramework.dll','PresentationCore.dll','WindowsBase.dll','System.Xaml.dll') | ForEach-Object {
    $p = Join-Path $wpf $_
    if (-not (Test-Path -LiteralPath $p)) { $p = Join-Path $framework $_ }
    '/reference:' + $p
}
$sources = Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' | ForEach-Object { $_.FullName }
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /utf8output /codepage:65001 /langversion:5 "/out:$out\CarbonControl.exe" /win32manifest:src/app.manifest /win32icon:assets/carbon.ico /resource:src/MainWindow.xaml,MainWindow.xaml /resource:assets/carbon.ico,carbon.ico $references $sources
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
Copy-Item -LiteralPath '.\scripts\setup-hardware.ps1' -Destination $out
$appConfig = Join-Path $out 'CarbonControl.exe.config'
if (-not (Test-Path -LiteralPath $appConfig)) {
    $previousConfig = Join-Path $previousApp 'CarbonControl.exe.config'
    if (Test-Path -LiteralPath $previousConfig) { Copy-Item -LiteralPath $previousConfig -Destination $appConfig }
    else { Copy-Item -LiteralPath '.\src\App.config' -Destination $appConfig }
}
Copy-Item -LiteralPath '.\README.md','.\THIRD-PARTY-NOTICES.txt' -Destination $out
if ($Test) {
    $testReport = Join-Path $PSScriptRoot 'test-results.txt'
    $process = Start-Process -FilePath (Join-Path $out 'CarbonControl.exe') -ArgumentList @('--self-test', ('"' + $testReport + '"')) -WindowStyle Hidden -Wait -PassThru
    Get-Content -LiteralPath $testReport -Encoding UTF8
    if ($process.ExitCode -ne 0) { throw 'Tests failed.' }
    $uiReport = Join-Path $PSScriptRoot 'ui-test-results.txt'
    $process = Start-Process -FilePath (Join-Path $out 'CarbonControl.exe') -ArgumentList @('--ui-test', ('"' + $uiReport + '"')) -WindowStyle Hidden -Wait -PassThru
    Get-Content -LiteralPath $uiReport -Encoding UTF8
    if ($process.ExitCode -ne 0) { throw 'UI tests failed.' }
}
if ($Preview) {
    $previews = Join-Path $PSScriptRoot 'previews'
    $process = Start-Process -FilePath (Join-Path $out 'CarbonControl.exe') -ArgumentList @('--render-preview', ('"' + $previews + '"')) -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw 'Preview rendering failed.' }
}
Write-Host ('Built: ' + (Join-Path $out 'CarbonControl.exe'))
