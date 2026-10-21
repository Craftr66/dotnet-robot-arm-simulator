param([Parameter(Mandatory=$true)][string]$TargetRoot, [Parameter(Mandatory=$true)][string]$ReleaseRoot, [switch]$Apply)
$ErrorActionPreference='Stop'
$frameworkWorkspace=Split-Path $PSScriptRoot -Parent
$frameworkSource=Join-Path $frameworkWorkspace 'src\WpfDemo'
$frameworkTarget=[System.IO.Path]::GetFullPath($TargetRoot)
$frameworkRelease=Join-Path $frameworkWorkspace 'dist\AxisLab.WpfDemo-1.1.0-win-x64'
$frameworkDesktopRelease=[System.IO.Path]::GetFullPath($ReleaseRoot)
$frameworkBaseline=Get-Content -LiteralPath (Join-Path $frameworkWorkspace 'artifacts\framework-import\original-files.json') -Raw | ConvertFrom-Json
$frameworkKnown=@{}
foreach($frameworkOriginal in $frameworkBaseline) {
    $frameworkOriginalPath=Join-Path $frameworkTarget $frameworkOriginal.Name
    if (!(Test-Path -LiteralPath $frameworkOriginalPath -PathType Leaf)) { throw "Original file disappeared: $frameworkOriginalPath" }
    if ((Get-FileHash -LiteralPath $frameworkOriginalPath -Algorithm SHA256).Hash -ne $frameworkOriginal.Sha256) { throw "Original file changed since import: $frameworkOriginalPath" }
    $frameworkKnown[$frameworkOriginal.Name]=$true
}
$frameworkSourceFiles=@(Get-ChildItem -LiteralPath $frameworkSource -Recurse -File | Where-Object { $_.FullName.Substring($frameworkSource.Length + 1) -notmatch '(^|[\\/])(bin|obj)([\\/]|$)' })
foreach($frameworkFile in $frameworkSourceFiles) {
    $frameworkRelative=$frameworkFile.FullName.Substring($frameworkSource.Length + 1)
    $frameworkDestination=Join-Path $frameworkTarget $frameworkRelative
    if ((Test-Path -LiteralPath $frameworkDestination) -and !$frameworkKnown.ContainsKey($frameworkRelative)) { throw "Unrecognized destination exists: $frameworkDestination" }
}
if (Test-Path -LiteralPath $frameworkDesktopRelease) { throw "New release directory already exists: $frameworkDesktopRelease" }
if (!(Test-Path -LiteralPath (Join-Path $frameworkRelease 'VmBridge\AxisLab.VmBridge.exe'))) { throw 'Published worker is missing.' }
$frameworkEvidence=Join-Path $frameworkWorkspace 'artifacts\wpf-framework-checks\1792571445911\results.json'
if (!(Get-Content -LiteralPath $frameworkEvidence -Raw | ConvertFrom-Json).success) { throw 'Published checks did not pass.' }
Write-Host "Preflight passed: $($frameworkSourceFiles.Count) source files; original hashes match; release destination is new."
if (!$Apply) { return }
$frameworkBackup=Join-Path $frameworkWorkspace ('artifacts\framework-import\source-backup-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $frameworkBackup | Out-Null
foreach($frameworkOriginal in $frameworkBaseline) { Copy-Item -LiteralPath (Join-Path $frameworkTarget $frameworkOriginal.Name) -Destination $frameworkBackup }
Copy-Item -LiteralPath (Join-Path $frameworkWorkspace 'artifacts\framework-import\original-files.json') -Destination (Join-Path $frameworkBackup 'original-files.json')
$frameworkReceipt=@()
foreach($frameworkFile in $frameworkSourceFiles) {
    $frameworkRelative=$frameworkFile.FullName.Substring($frameworkSource.Length + 1)
    $frameworkDestination=Join-Path $frameworkTarget $frameworkRelative
    New-Item -ItemType Directory -Path (Split-Path $frameworkDestination -Parent) -Force | Out-Null
    Copy-Item -LiteralPath $frameworkFile.FullName -Destination $frameworkDestination -Force
    $frameworkExpectedHash=(Get-FileHash -LiteralPath $frameworkFile.FullName -Algorithm SHA256).Hash
    if ((Get-FileHash -LiteralPath $frameworkDestination -Algorithm SHA256).Hash -ne $frameworkExpectedHash) { throw "Source verification failed: $frameworkRelative" }
    $frameworkReceipt+=@{path=$frameworkRelative;sha256=$frameworkExpectedHash}
}
Copy-Item -LiteralPath $frameworkRelease -Destination $frameworkDesktopRelease -Recurse
$frameworkReleaseCount=0
foreach($frameworkFile in (Get-ChildItem -LiteralPath $frameworkRelease -Recurse -File)) {
    $frameworkRelative=$frameworkFile.FullName.Substring($frameworkRelease.Length + 1)
    if ((Get-FileHash -LiteralPath $frameworkFile.FullName -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath (Join-Path $frameworkDesktopRelease $frameworkRelative) -Algorithm SHA256).Hash) { throw "Release verification failed: $frameworkRelative" }
    $frameworkReleaseCount++
}
@{source=$frameworkTarget;release=$frameworkDesktopRelease;backup=$frameworkBackup;checks=$frameworkEvidence;sourceFiles=$frameworkReceipt;releaseFilesVerified=$frameworkReleaseCount} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $frameworkWorkspace 'artifacts\framework-import\deployment.json') -Encoding UTF8
Write-Host "Source updated: $frameworkTarget"
Write-Host "Original backup: $frameworkBackup"
Write-Host "Release: $frameworkDesktopRelease"
Write-Host "Verified $($frameworkReceipt.Count) source files and $frameworkReleaseCount release files."

