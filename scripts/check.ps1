$ErrorActionPreference = 'Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    & node node_modules/typescript/bin/tsc --noEmit -p src/Frontend/tsconfig.json
    if ($LASTEXITCODE -ne 0) { throw 'TypeScript validation failed.' }
    & dotnet build src/Host/AxisLab.Host.csproj --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Host build failed.' }
    & dotnet run --project tests/CoreChecks/AxisLab.CoreChecks.csproj
    if ($LASTEXITCODE -ne 0) { throw 'Core checks failed.' }
} finally { Pop-Location }
