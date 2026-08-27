$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$publishDir = Join-Path $projectRoot "publish\win-x64"
$dotnet = "dotnet"

if (-not (Get-Command $dotnet -ErrorAction SilentlyContinue)) {
    $standardDotnetPath = "C:\Program Files\dotnet\dotnet.exe"
    if (-not (Test-Path $standardDotnetPath)) {
        throw "dotnet was not found on PATH or at $standardDotnetPath"
    }

    $dotnet = $standardDotnetPath
}

if (Test-Path $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}

function Assert-Success {
    param([string] $Step)

    if ($LASTEXITCODE -ne 0) {
        throw "$Step failed with exit code $LASTEXITCODE"
    }
}

& $dotnet build $projectRoot -c Release
Assert-Success "dotnet build"

& $dotnet publish $projectRoot `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o $publishDir
Assert-Success "dotnet publish"

Write-Host "Published to: $publishDir"
