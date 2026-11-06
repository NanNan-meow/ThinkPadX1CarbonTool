# Invoked by the application's hardware setup button. Installs the official signed PawnIO edition.
param([switch]$DownloadOnly)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = $PSScriptRoot
$log = Join-Path $root 'setup-hardware.log'
try {
    Start-Transcript -LiteralPath $log -Force | Out-Null
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $downloads = Join-Path $root 'downloads'
    New-Item -ItemType Directory -Path $downloads -Force | Out-Null
    function Get-VerifiedFile([string]$Url, [string]$Destination, [string]$Sha256) {
        if ((Test-Path -LiteralPath $Destination) -and ((Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash -eq $Sha256)) { return }
        Invoke-WebRequest -Uri $Url -OutFile $Destination -UseBasicParsing -TimeoutSec 180
        if ((Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash -ne $Sha256) { throw ('Hash mismatch: ' + $Destination) }
    }
    $archive = Join-Path $downloads 'LibreHardwareMonitor-0.9.6.zip'
    Get-VerifiedFile 'https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/releases/download/v0.9.6/LibreHardwareMonitor.zip' $archive '086d9f1b5a99e643edc2cfaaac16051685b551e4c5ac0b32a57c58c0e529c001'
    $installer = Join-Path $downloads 'PawnIO-2.2.0.exe'
    Get-VerifiedFile 'https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe' $installer '1f519a22e47187f70a1379a48ca604981c4fcf694f4e65b734aaa74a9fba3032'
    $hardware = Join-Path $root 'hardware'
    Expand-Archive -LiteralPath $archive -DestinationPath $hardware -Force
    if (-not (Test-Path -LiteralPath (Join-Path $hardware 'LibreHardwareMonitorLib.dll'))) { throw 'LibreHardwareMonitorLib.dll was not found in the archive.' }
    # Preserve upstream assembly redirects while adding the private hardware directory.
    $configPath = Join-Path $root 'CarbonControl.exe.config'
    $upstreamConfig = Join-Path $hardware 'LibreHardwareMonitor.exe.config'
    if (Test-Path -LiteralPath $upstreamConfig) {
        [xml]$config = Get-Content -LiteralPath $upstreamConfig -Raw
        $runtime = $config.configuration.runtime
        if (-not $runtime) { $runtime = $config.CreateElement('runtime'); $config.configuration.AppendChild($runtime) | Out-Null }
        $binding = $runtime.SelectSingleNode("*[local-name()='assemblyBinding']")
        if (-not $binding) { $binding = $config.CreateElement('assemblyBinding','urn:schemas-microsoft-com:asm.v1'); $runtime.AppendChild($binding) | Out-Null }
        $probing = $config.CreateElement('probing','urn:schemas-microsoft-com:asm.v1'); $probing.SetAttribute('privatePath','hardware'); $binding.AppendChild($probing) | Out-Null
        $config.Save($configPath)
    }
    if (-not $DownloadOnly) {
        $installed = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO' -ErrorAction SilentlyContinue
        if (-not $installed -or ([version]$installed.DisplayVersion -lt [version]'2.2.0')) {
            $signature = Get-AuthenticodeSignature -LiteralPath $installer
            if ($signature.Status -ne 'Valid') { throw ('PawnIO installer signature: ' + $signature.Status) }
            # Show the official installer, so the signed edition and install outcome are visible.
            $process = Start-Process -FilePath $installer -Wait -PassThru
            if ($process.ExitCode -notin @(0,3010)) { throw ('PawnIO installer returned ' + $process.ExitCode) }
        }
        $installed = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO' -ErrorAction Stop
        if ([version]$installed.DisplayVersion -lt [version]'2.2.0') { throw 'PawnIO 2.2.0 installation was not completed.' }
    }
    # Validate in a new process so the updated runtime configuration is actually used.
    $application = Join-Path $root 'CarbonControl.exe'
    if (Test-Path -LiteralPath $application) {
        $report = Join-Path $root 'hardware-components-check.txt'
        $check = Start-Process -FilePath $application -ArgumentList @('--check-components', ('"' + $report + '"')) -WindowStyle Hidden -Wait -PassThru
        if ($check.ExitCode -ne 0) { throw ('Hardware component load check failed. See ' + $report) }
    }
    Write-Host 'Hardware components verified. Carbon Control will restart to reload them.'
    Stop-Transcript | Out-Null
    exit 0
} catch {
    Write-Output $_.Exception.ToString()
    try { Stop-Transcript | Out-Null } catch { }
    exit 1
}
