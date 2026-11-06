param(
    [Parameter(Mandatory = $true)][ValidatePattern('^v\d+\.\d+\.\d+$')][string]$Tag,
    [Parameter(Mandatory = $true)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.-]*/[A-Za-z0-9][A-Za-z0-9_.-]*$')][string]$Repository,
    [switch]$PlanOnly
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
function Invoke-GhJson([string[]]$Arguments) {
    $output = & gh @Arguments
    if ($LASTEXITCODE -ne 0) { throw ('GitHub request failed: ' + ($Arguments -join ' ')) }
    return (($output -join "`n") | ConvertFrom-Json)
}
$version = [version]$Tag.Substring(1)
$current = Invoke-GhJson @('release','view',$Tag,'--repo',$Repository,'--json','tagName,isDraft,isPrerelease,publishedAt,assets')
if ($current.tagName -cne $Tag -or $current.isDraft -or $current.isPrerelease -or -not $current.publishedAt) {
    throw 'The release to retain must be published and stable.'
}
$archiveName = 'CarbonControl-' + $Tag + '-win-x64.zip'
foreach ($name in @($archiveName, 'SHA256SUMS.txt')) {
    $asset = @($current.assets | Where-Object { $_.name -ceq $name })
    if ($asset.Count -ne 1 -or [long]$asset[0].size -le 0) { throw ('Published asset is missing or empty: ' + $name) }
}
$latest = Invoke-GhJson @('api',"repos/$Repository/releases/latest")
if ($latest.tag_name -cne $Tag) { throw 'The retained release is not Latest; older releases were not removed.' }
$pages = Invoke-GhJson @('api',"repos/$Repository/releases?per_page=100",'--paginate','--slurp')
$older = @()
foreach ($page in $pages) {
    foreach ($release in $page) {
        if ($null -eq $release -or $release.draft -or $release.prerelease -or $release.tag_name -cnotmatch '^v\d+\.\d+\.\d+$') { continue }
        $candidate = [version]$release.tag_name.Substring(1)
        if ($candidate -gt $version) { throw 'A newer stable release exists; older releases were not removed.' }
        if ($candidate -lt $version) { $older += [pscustomobject]@{ Tag = $release.tag_name; Version = $candidate } }
    }
}

$downloadDirectory = Join-Path ([IO.Path]::GetTempPath()) ('CarbonControl-release-check-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $downloadDirectory | Out-Null
$archivePath = Join-Path $downloadDirectory $archiveName
$checksumPath = Join-Path $downloadDirectory 'SHA256SUMS.txt'
try {
    & gh release download $Tag --repo $Repository --pattern $archiveName --pattern 'SHA256SUMS.txt' --dir $downloadDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Published asset download failed; older releases were not removed.' }
    $checksumLine = @(Get-Content -LiteralPath $checksumPath | Where-Object {
        $_ -cmatch ('^[0-9a-fA-F]{64}\s+\*?' + [regex]::Escape($archiveName) + '$')
    })
    if ($checksumLine.Count -ne 1) { throw 'The published checksum does not identify the new ZIP exactly once.' }
    $expected = ($checksumLine[0] -split '\s+')[0]
    $actual = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
    if ($actual -ine $expected) { throw 'Published ZIP checksum mismatch; older releases were not removed.' }
} finally {
    foreach ($file in @($archivePath, $checksumPath)) {
        if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file -Force }
    }
    [IO.Directory]::Delete($downloadDirectory, $false)
}

# Recheck after the downloads in case another version was published meanwhile.
$latest = Invoke-GhJson @('api',"repos/$Repository/releases/latest")
if ($latest.tag_name -cne $Tag) { throw 'Latest changed during verification; older releases were not removed.' }
foreach ($release in @($older | Sort-Object Version -Descending)) {
    if ($PlanOnly) { Write-Host ('Would remove release: ' + $release.Tag); continue }
    & gh release delete $release.Tag --repo $Repository --yes
    if ($LASTEXITCODE -ne 0) { throw ('Could not remove release: ' + $release.Tag) }
    Write-Host ('Removed release: ' + $release.Tag)
}
Write-Host ('Verified and retained release: ' + $Tag)
