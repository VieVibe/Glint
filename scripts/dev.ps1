param(
    [ValidateSet('doctor', 'restore', 'build', 'run', 'check', 'publish')]
    [string]$Action = 'build'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$localDotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
$command = Get-Command dotnet -ErrorAction SilentlyContinue
if ($command) {
    $dotnet = $command.Source
} elseif (Test-Path -LiteralPath $localDotnet) {
    $dotnet = $localDotnet
} else {
    throw '未找到 .NET SDK。请按 README 安装 .NET 10 SDK。'
}

$env:DOTNET_ROOT = Split-Path -Parent $dotnet
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"

Push-Location $projectRoot
try {
    switch ($Action) {
        'doctor' {
            & $dotnet --version
            if ($LASTEXITCODE -ne 0) { throw '项目要求的 SDK 不可用，请检查 global.json。' }
            & $dotnet --info
        }
        'restore' { & $dotnet restore Glint.slnx }
        'build' { & $dotnet build Glint.slnx --configuration Debug }
        'run' { & $dotnet run --project src/Glint.App/Glint.App.csproj }
        'check' { & $dotnet run --project tests/Glint.Checks/Glint.Checks.csproj --configuration Release }
        'publish' {
            & $dotnet publish src/Glint.App/Glint.App.csproj --configuration Release --runtime win-x64 --self-contained true --output artifacts/Glint-win-x64
            if ($LASTEXITCODE -eq 0) {
                $runtimeConfig = Get-Content -Raw artifacts/Glint-win-x64/Glint.runtimeconfig.json | ConvertFrom-Json
                $packageRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget/packages' }
                foreach ($framework in $runtimeConfig.runtimeOptions.includedFrameworks) {
                    $packName = $framework.name.ToLowerInvariant() + '.runtime.win-x64'
                    $packDirectory = Join-Path (Join-Path $packageRoot $packName) $framework.version
                    $licenseDirectory = Join-Path 'artifacts/Glint-win-x64/licenses' $framework.name
                    New-Item -ItemType Directory -Path $licenseDirectory -Force | Out-Null
                    Get-ChildItem -LiteralPath $packDirectory -File | Where-Object { $_.Name -like 'LICENSE*' -or $_.Name -like 'THIRD-PARTY-NOTICES*' } |
                        Copy-Item -Destination $licenseDirectory
                }
            }
        }
    }
    if ($LASTEXITCODE -ne 0) { throw ".NET 执行失败，退出码：$LASTEXITCODE" }
} finally {
    Pop-Location
}
