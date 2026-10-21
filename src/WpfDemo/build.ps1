param([ValidateSet('Debug','Release')][string]$Configuration='Debug')
$ErrorActionPreference='Stop'
Push-Location $PSScriptRoot
try {
    & dotnet build AxisLab.WpfDemo.csproj -c $Configuration -m:1 -nr:false
    if ($LASTEXITCODE -ne 0) { throw 'WPF build failed.' }
    & dotnet build VmBridge/AxisLab.VmBridge.csproj -c $Configuration -m:1 -nr:false
    if ($LASTEXITCODE -ne 0) { throw 'VM Bridge build failed.' }
    $demoBridgeOutput=Join-Path $PSScriptRoot "bin\$Configuration\net9.0-windows\VmBridge"
    New-Item -ItemType Directory -Path $demoBridgeOutput -Force | Out-Null
    Copy-Item -LiteralPath "VmBridge\bin\$Configuration\net48\AxisLab.VmBridge.exe","VmBridge\bin\$Configuration\net48\AxisLab.VmBridge.exe.config" -Destination $demoBridgeOutput -Force
    Write-Host "Application: $PSScriptRoot\bin\$Configuration\net9.0-windows\AxisLab.WpfDemo.exe"
} finally { Pop-Location }
