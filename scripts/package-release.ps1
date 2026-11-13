param(
    [string]$OutputDirectory = 'artifacts/releases'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$projectRoot = Split-Path -Parent $PSScriptRoot
$command = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnet = if ($command) { $command.Source } else { Join-Path $env:LOCALAPPDATA 'Microsoft/dotnet/dotnet.exe' }
if (-not (Test-Path -LiteralPath $dotnet)) { throw 'Install the .NET SDK specified in global.json first.' }
$env:DOTNET_ROOT = Split-Path -Parent $dotnet
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"

Push-Location $projectRoot
try {
    $status = @(git status --porcelain --untracked-files=all)
    if ($LASTEXITCODE -ne 0 -or $status.Count -gt 0) { throw 'Commit source changes before creating a release package.' }
    $revision = git rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Unable to resolve the source commit.' }
    [xml]$project = Get-Content -Raw -LiteralPath src/Glint.App/Glint.App.csproj
    $version = [string]$project.Project.PropertyGroup.Version
    if ($version -notmatch '^\d+\.\d+\.\d+(?:-[a-zA-Z0-9.-]+)?$') { throw 'Set a semantic Version in Glint.App.csproj.' }
    $releaseRoot = [System.IO.Path]::GetFullPath((Join-Path $OutputDirectory $version))
    if (Test-Path -LiteralPath $releaseRoot) { throw "Release output already exists: $releaseRoot. Choose a different OutputDirectory to rebuild." }
    $stage = Join-Path $releaseRoot 'Glint-win-x64'
    New-Item -ItemType Directory -Path $stage -Force | Out-Null

    & $dotnet publish src/Glint.App/Glint.App.csproj --configuration Release --runtime win-x64 --self-contained true `
        --output $stage -p:DebugType=None -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }

    foreach ($notice in @('LICENSE', 'THIRD-PARTY-NOTICES.md', 'README.md')) {
        if (-not (Test-Path -LiteralPath (Join-Path $stage $notice) -PathType Leaf)) {
            throw "Required license or notice missing from release: $notice"
        }
    }

    $runtimeConfig = Get-Content -Raw -LiteralPath (Join-Path $stage 'Glint.runtimeconfig.json') | ConvertFrom-Json
    foreach ($notice in @('licenses/WindowsSDK/sdk_license.rtf', 'licenses/CsWinRT/LICENSE.txt', 'licenses/Markdig/LICENSE.txt')) {
        if (-not (Test-Path -LiteralPath (Join-Path $stage $notice) -PathType Leaf)) {
            throw "Required dependency license missing from release: $notice"
        }
    }
    $packageRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget/packages' }
    foreach ($framework in $runtimeConfig.runtimeOptions.includedFrameworks) {
        $pack = Join-Path (Join-Path $packageRoot ($framework.name.ToLowerInvariant() + '.runtime.win-x64')) $framework.version
        $destination = Join-Path (Join-Path $stage 'licenses') $framework.name
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        $licenses = @(Get-ChildItem -LiteralPath $pack -File | Where-Object { $_.Name -like 'LICENSE*' -or $_.Name -like 'THIRD-PARTY-NOTICES*' })
        if ($licenses.Count -eq 0) { throw "Runtime license files missing: $($framework.name)" }
        $licenses | Copy-Item -Destination $destination
    }

    $usage = [System.IO.File]::ReadAllText((Join-Path $projectRoot 'docs/usage.zh-CN.txt'))
    [System.IO.File]::WriteAllText((Join-Path $stage 'USAGE.txt'), $usage, [System.Text.UTF8Encoding]::new($true))
    [System.IO.File]::WriteAllText((Join-Path $stage 'BUILD-INFO.txt'), "Glint $version`nSource: $revision`nRuntime: win-x64 (self-contained)`n")

    $archive = Join-Path $releaseRoot 'Glint-win-x64.zip'
    [System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $archive, [System.IO.Compression.CompressionLevel]::Optimal, $true)
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    [System.IO.File]::WriteAllText((Join-Path $releaseRoot 'SHA256SUMS.txt'), "$hash  Glint-win-x64.zip`n")
    Write-Output "Package: $archive"
    Write-Output "SHA256: $hash"
    Write-Output "Source: $revision"
} finally {
    Pop-Location
}
