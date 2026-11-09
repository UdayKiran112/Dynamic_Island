param(
    [switch]$SelfContained,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "src/DynamicIsland/DynamicIsland.csproj"
$output = Join-Path $PSScriptRoot "publish"

Write-Host "Publishing Dynamic Island ($Configuration)..." -ForegroundColor Cyan

$arguments = @(
    "publish", $project,
    "-c", $Configuration,
    "-r", "win-x64",
    "--self-contained", $SelfContained.IsPresent.ToString().ToLowerInvariant(),
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:DebugType=None",
    "-p:DebugSymbols=false",
    "-o", $output
)

& dotnet @arguments
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$exe = Join-Path $output "DynamicIsland.exe"
if (Test-Path $exe) {
    $size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
    Write-Host "Done: $exe ($size MB)" -ForegroundColor Green
}
