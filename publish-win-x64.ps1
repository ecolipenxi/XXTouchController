$ErrorActionPreference = "Stop"
$projectDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnetCommand) {
    throw "Không tìm thấy .NET 8 SDK. Cài .NET 8 SDK rồi chạy lại."
}

$releaseDirectory = Join-Path $projectDirectory "release"
if (Test-Path -LiteralPath $releaseDirectory) {
    Remove-Item -LiteralPath $releaseDirectory -Recurse -Force
}

dotnet restore $projectDirectory
dotnet build $projectDirectory -c Release --no-restore
dotnet publish $projectDirectory -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None -p:DebugSymbols=false -o $releaseDirectory

Copy-Item -LiteralPath (Join-Path $projectDirectory "README-VI.md") `
    -Destination $releaseDirectory -Force
Write-Host "Đã tạo bản phát hành tại: $releaseDirectory"
