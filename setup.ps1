[CmdletBinding()]
param()
$ErrorActionPreference = "Stop"
Write-Host "Studio.exe setup checks" -ForegroundColor Cyan
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    throw ".NET 10 SDK was not found. Install it from https://dotnet.microsoft.com/download/dotnet/10.0 and run setup.ps1 again."
}
$version = (& dotnet --version).Trim()
Write-Host "Detected .NET SDK: $version" -ForegroundColor Green
if ($version -notmatch '^10\.') { Write-Warning "Studio.exe targets .NET 10. Detected SDK: $version" }
$node = Get-Command node -ErrorAction SilentlyContinue
$npm = Get-Command npm -ErrorAction SilentlyContinue
if (-not $node -or -not $npm) {
    throw "Node.js 22+ and npm are required to prepare the bundled Monaco editor. Install Node.js from https://nodejs.org/ and rerun setup.ps1."
}
Write-Host "Detected Node.js: $((& node --version).Trim())" -ForegroundColor Green
Write-Host "No administrator privileges are required. The build installs the pinned Monaco editor package into the workspace." -ForegroundColor Green
