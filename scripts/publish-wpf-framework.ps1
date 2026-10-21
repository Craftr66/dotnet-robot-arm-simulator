param([switch]$NoRestore)
$ErrorActionPreference = 'Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    & dotnet build src/WpfDemo/VmBridge/AxisLab.VmBridge.csproj -c Release -m:1 -nr:false --packages .cache/nuget-packages
    if ($LASTEXITCODE -ne 0) { throw 'VM bridge build failed.' }
    $frameworkOutput = Join-Path $PWD 'dist\AxisLab.WpfDemo-1.2.0-win-x64'
    $frameworkRestoreArgs = @()
    if ($NoRestore) { $frameworkRestoreArgs += '--no-restore' }
    & dotnet publish src/WpfDemo/AxisLab.WpfDemo.csproj -c Release -r win-x64 --self-contained true -p:RuntimeFrameworkVersion=9.0.20 `
        -p:RestoreSources=https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public/nuget/v3/index.json `
        --packages .cache/nuget-packages -m:1 -nr:false -o $frameworkOutput @frameworkRestoreArgs
    if ($LASTEXITCODE -ne 0) { throw 'WPF framework publish failed.' }
    $frameworkBridge = Join-Path $frameworkOutput 'VmBridge'
    New-Item -ItemType Directory -Path $frameworkBridge -Force | Out-Null
    Copy-Item -LiteralPath src\WpfDemo\VmBridge\bin\Release\net48\AxisLab.VmBridge.exe,src\WpfDemo\VmBridge\bin\Release\net48\AxisLab.VmBridge.exe.config -Destination $frameworkBridge -Force
    Copy-Item -LiteralPath docs\原WPF框架集成说明.md -Destination (Join-Path $frameworkOutput '使用说明.md') -Force
    Copy-Item -LiteralPath docs\原WPF框架集成验收.md -Destination $frameworkOutput -Force
    New-Item -ItemType Directory -Path (Join-Path $frameworkOutput 'Samples') -Force | Out-Null
    Copy-Item -LiteralPath samples\vm-bridge.scene.json,samples\nine-point-calibration.scene.json -Destination (Join-Path $frameworkOutput 'Samples') -Force
    Copy-Item -LiteralPath docs\VM九点标定与WPF集成教程.md,docs\阶段7B验收记录.md -Destination $frameworkOutput -Force
    $frameworkSourceRoot = (Resolve-Path -LiteralPath src/WpfDemo).Path
    foreach ($frameworkFile in (Get-ChildItem -LiteralPath $frameworkSourceRoot -Recurse -File)) {
        $frameworkRelative = $frameworkFile.FullName.Substring($frameworkSourceRoot.Length + 1)
        if ($frameworkRelative -match '(^|[\\/])(bin|obj)([\\/]|$)') { continue }
        $frameworkDestination = Join-Path (Join-Path $frameworkOutput 'Source') $frameworkRelative
        New-Item -ItemType Directory -Path (Split-Path $frameworkDestination -Parent) -Force | Out-Null
        Copy-Item -LiteralPath $frameworkFile.FullName -Destination $frameworkDestination -Force
    }
    $frameworkArchive = Join-Path $PWD 'dist\AxisLab.WpfDemo-1.2.0-win-x64.zip'
    Compress-Archive -LiteralPath $frameworkOutput -DestinationPath $frameworkArchive -Force
    Get-FileHash -LiteralPath $frameworkArchive -Algorithm SHA256 | Format-List
    Write-Host "Application: $frameworkOutput\AxisLab.WpfDemo.exe"
} finally { Pop-Location }
