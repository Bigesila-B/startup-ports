[CmdletBinding()]
param(
    [string]$Dotnet = 'dotnet',
    [string]$InnoCompiler
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $projectRoot 'src/StartupPorts/StartupPorts.csproj'
$publishRoot = Join-Path $projectRoot 'artifacts/publish'
$releaseRoot = Join-Path $projectRoot 'artifacts/release'
[xml]$metadata = Get-Content -LiteralPath $project
$version = [string]$metadata.Project.PropertyGroup.Version

if (-not $InnoCompiler) {
    $compiler = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    $InnoCompiler = $compiler.Source
}
if (-not $InnoCompiler) {
    $locations = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 7\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 7\ISCC.exe"
    )
    $InnoCompiler = $locations | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
}
if (-not $InnoCompiler) {
    throw 'Inno Setup is required. Set -InnoCompiler to the full path of ISCC.exe.'
}

Push-Location $projectRoot
try {
    & $Dotnet publish $project -c Release -r win-x64 --self-contained true -o $publishRoot -p:DebugType=none
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

    & $InnoCompiler "/DAppVersion=$version" (Join-Path $projectRoot 'installer/StartupPorts.iss')
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed with exit code $LASTEXITCODE." }

    $file = Get-Item -LiteralPath (Join-Path $releaseRoot "StartupPorts-$version-win-x64-setup.exe")
    $digest = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$digest  $($file.Name)" | Set-Content -LiteralPath (Join-Path $releaseRoot 'SHA256SUMS.txt') -Encoding ascii
    Write-Output $file.FullName
}
finally {
    Pop-Location
}
