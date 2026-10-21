param([switch]$SkipFrontend, [switch]$NoRestore)
$ErrorActionPreference = 'Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    if (-not $SkipFrontend) {
        & node node_modules/typescript/bin/tsc --noEmit -p src/Frontend/tsconfig.json
        if ($LASTEXITCODE -ne 0) { throw 'TypeScript check failed.' }
        & node scripts/build.mjs
        if ($LASTEXITCODE -ne 0) { throw 'Frontend build failed.' }
    }
    $version = ([xml](Get-Content -LiteralPath src\Desktop\AxisLab.Desktop.csproj -Raw)).Project.PropertyGroup.Version
    $output = Join-Path $PWD "dist\AxisLab-$version"
    $restoreArguments = @()
    if ($NoRestore) { $restoreArguments += '--no-restore' }
    & dotnet publish src/Desktop/AxisLab.Desktop.csproj -c Release -r win-x64 --self-contained true `
        -m:1 -nr:false `
        -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=None `
        --packages .cache/nuget-packages -o $output --nologo @restoreArguments
    if ($LASTEXITCODE -ne 0) { throw 'Desktop publish failed.' }
    Copy-Item -LiteralPath docs\桌面版使用说明.md -Destination (Join-Path $output '使用说明.md') -Force
    Copy-Item -LiteralPath docs\相机与尺寸使用说明.md,docs\相机HTTP接口.md -Destination $output -Force
    Copy-Item -LiteralPath docs\外部连接快速开始.md,docs\ModbusTCP操作协议v1.md,docs\Modbus-v1-map.json,docs\三轴机械臂操作说明.md -Destination $output -Force
    Copy-Item -LiteralPath docs\运动与示教使用说明.md,docs\运动与示教接口.md,docs\阶段5验收记录.md -Destination $output -Force
    Copy-Item -LiteralPath docs\积木编程使用说明.md,docs\积木程序接口.md,docs\阶段6验收记录.md,docs\桌面版验收记录.md -Destination $output -Force
    Copy-Item -LiteralPath docs\VM九点标定与WPF集成教程.md,docs\阶段7B验收记录.md -Destination $output -Force
    Get-ChildItem -LiteralPath docs -Filter '桌面版0.*验收记录.md' -File | Copy-Item -Destination $output -Force
    $licenses = Join-Path $output 'Licenses'
    New-Item -ItemType Directory -Path $licenses -Force | Out-Null
    Copy-Item -LiteralPath node_modules\three\LICENSE -Destination (Join-Path $licenses 'Three.js-MIT.txt') -Force
    Copy-Item -LiteralPath node_modules\blockly\LICENSE -Destination (Join-Path $licenses 'Blockly-Apache-2.0.txt') -Force
    $webViewLicense = Get-ChildItem -LiteralPath .cache\nuget-packages\microsoft.web.webview2\1.0.4258.31 -Filter '*license*' -File
    foreach ($license in $webViewLicense) { Copy-Item -LiteralPath $license.FullName -Destination $licenses -Force }
    Copy-Item -LiteralPath .cache\nuget-packages\microsoft.web.webview2\1.0.4258.31\NOTICE.txt -Destination (Join-Path $licenses 'WebView2-NOTICE.txt') -Force
    foreach ($runtime in @('microsoft.netcore.app.runtime.win-x64', 'microsoft.windowsdesktop.app.runtime.win-x64', 'microsoft.aspnetcore.app.runtime.win-x64')) {
        $runtimeRoot = Join-Path '.cache\nuget-packages' "$runtime\10.0.12"
        foreach ($licenseName in @('LICENSE', 'LICENSE.txt', 'THIRD-PARTY-NOTICES.TXT', 'THIRD-PARTY-NOTICES.txt')) {
            $licenseFile = Join-Path $runtimeRoot $licenseName
            if (Test-Path -LiteralPath $licenseFile) {
                Copy-Item -LiteralPath $licenseFile -Destination (Join-Path $licenses "$runtime-$licenseName") -Force
            }
        }
    }
    $archive = Join-Path $PWD "dist\AxisLab-$version-win-x64.zip"
    Compress-Archive -LiteralPath $output -DestinationPath $archive -Force
    Get-FileHash -LiteralPath $archive -Algorithm SHA256 | Format-List
    Write-Host "Application: $output\AxisLab.exe" -ForegroundColor Cyan
    Write-Host "Archive: $archive"
} finally { Pop-Location }
