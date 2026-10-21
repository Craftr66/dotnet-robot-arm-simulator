param(
    [ValidateRange(1024, 65535)][int]$Port = 5080,
    [switch]$NoBuild
)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Please install .NET 10 SDK.' }
    if (-not $NoBuild) {
        if (-not (Get-Command node -ErrorAction SilentlyContinue)) { throw 'Please install Node.js 22 or newer.' }
        if (-not (Test-Path -LiteralPath 'node_modules/esbuild/package.json')) {
            throw 'Frontend dependencies are missing. Run pnpm install (or npm install), then start again.'
        }
        & node node_modules/typescript/bin/tsc --noEmit -p src/Frontend/tsconfig.json
        if ($LASTEXITCODE -ne 0) { throw 'TypeScript validation failed.' }
        & node scripts/build.mjs
        if ($LASTEXITCODE -ne 0) { throw 'Frontend build failed.' }
        & dotnet build src/Host/AxisLab.Host.csproj --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Core build failed.' }
    }
    if (-not (Test-Path -LiteralPath 'src/Host/wwwroot/index.html')) { throw 'Frontend is not built. Run without -NoBuild.' }
    $env:AXISLAB_URL = "http://127.0.0.1:$Port"
    Write-Host "AxisLab: $env:AXISLAB_URL" -ForegroundColor Cyan
    Write-Host 'Open this address in Edge or Chrome. Press Ctrl+C to stop.'
    & dotnet run --project src/Host/AxisLab.Host.csproj --no-build --no-launch-profile
    if ($LASTEXITCODE -ne 0) { throw 'Service stopped with an error. Check whether the port is occupied.' }
} finally { Pop-Location }
