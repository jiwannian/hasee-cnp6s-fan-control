param([string]$Output = (Join-Path $PSScriptRoot 'artifacts\publish'))
$ErrorActionPreference = 'Stop'
Push-Location -LiteralPath $PSScriptRoot
try {
    $Output = [System.IO.Path]::GetFullPath($Output)
    dotnet run --project '.\tests\FanControl.Tests' --configuration Release
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
    dotnet publish '.\src\CpuFanControl\CpuFanControl.csproj' --configuration Release --output $Output
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
    Copy-Item -LiteralPath '.\README.md' -Destination (Join-Path $Output '使用说明.md') -Force
    Copy-Item -LiteralPath '.\LICENSE' -Destination (Join-Path $Output 'LICENSE') -Force
    Write-Host (Join-Path $Output 'CpuFanControl.exe')
}
finally {
    Pop-Location
}
