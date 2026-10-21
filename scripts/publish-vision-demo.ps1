param([switch]$NoBuild, [string]$VmRoot = 'C:\Program Files\VisionMaster4.3.0')
$ErrorActionPreference = 'Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    if (-not $NoBuild) {
        & dotnet build src/VisionDemo/AxisLab.VisionDemo.csproj -c Release -m:1 -nr:false --packages .cache/nuget-packages "-p:VmRoot=$VmRoot"
        if ($LASTEXITCODE -ne 0) { throw 'Vision Demo build failed.' }
    }
    $demoOutput = Join-Path $PWD 'dist\AxisLab-VisionDemo-0.1.0'
    New-Item -ItemType Directory -Path $demoOutput -Force | Out-Null
    Copy-Item -LiteralPath src\VisionDemo\bin\Release\net48\AxisLab.VisionDemo.exe,src\VisionDemo\bin\Release\net48\AxisLab.VisionDemo.exe.config -Destination $demoOutput -Force
    Copy-Item -LiteralPath docs\VisionMaster与WPF使用说明.md -Destination (Join-Path $demoOutput '使用说明.md') -Force
    Copy-Item -LiteralPath docs\VisionMaster与WPF集成设计.md,docs\VisionMaster接入验收记录.md -Destination $demoOutput -Force
    $demoSamples = Join-Path $demoOutput 'Samples'
    $demoSource = Join-Path $demoOutput 'Source'
    New-Item -ItemType Directory -Path $demoSamples,$demoSource -Force | Out-Null
    Copy-Item -LiteralPath samples\vm-bridge.scene.json -Destination $demoSamples -Force
    Get-ChildItem -LiteralPath src\VisionDemo -File | Where-Object { $_.Extension -in '.cs', '.csproj', '.xaml' } | Copy-Item -Destination $demoSource -Force
    $demoArchive = Join-Path $PWD 'dist\AxisLab-VisionDemo-0.1.0-win-x64.zip'
    Compress-Archive -LiteralPath $demoOutput -DestinationPath $demoArchive -Force
    Get-FileHash -LiteralPath $demoArchive -Algorithm SHA256 | Format-List
    Write-Host "Demo: $demoOutput\AxisLab.VisionDemo.exe"
} finally { Pop-Location }
