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
Write-Host "No administrator privileges or third-party runtime downloads are required by this setup script." -ForegroundColor Green
