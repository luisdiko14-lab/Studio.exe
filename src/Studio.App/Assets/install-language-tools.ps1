[CmdletBinding()]
param()
$ErrorActionPreference = "Stop"
$toolsRoot = Join-Path $env:LOCALAPPDATA "Studio.exe\tools"
$jsRoot = Join-Path $toolsRoot "js-debug"
$netcoreRoot = Join-Path $toolsRoot "netcoredbg"
New-Item -ItemType Directory -Force -Path $toolsRoot, $jsRoot, $netcoreRoot | Out-Null

function Invoke-CheckedNative {
    param([string]$File, [string[]]$Arguments, [string]$Failure)
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Failure (exit code $LASTEXITCODE)" }
}

Write-Host "[1/5] Installing Pyright..." -ForegroundColor Cyan
if (-not (Get-Command npm -ErrorAction SilentlyContinue)) { throw "Node.js/npm is required. Install Node.js 22+ first." }
Invoke-CheckedNative "npm" @("install", "--global", "pyright") "Pyright installation failed"

Write-Host "[2/5] Installing Roslyn language server..." -ForegroundColor Cyan
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw ".NET 10 SDK is required." }
& dotnet tool update --global roslyn-language-server --prerelease
if ($LASTEXITCODE -ne 0) {
    Invoke-CheckedNative "dotnet" @("tool", "install", "--global", "roslyn-language-server", "--prerelease") "Roslyn language server installation failed"
}

Write-Host "[3/5] Installing Python debugpy..." -ForegroundColor Cyan
$python = Get-Command python -ErrorAction SilentlyContinue
if (-not $python) { throw "Python is required to install debugpy." }
Invoke-CheckedNative $python.Source @("-m", "pip", "install", "--user", "--upgrade", "debugpy") "debugpy installation failed"

Write-Host "[4/5] Installing Microsoft JavaScript DAP adapter..." -ForegroundColor Cyan
$headers = @{ "User-Agent" = "Studio.exe"; "Accept" = "application/vnd.github+json" }
$jsRelease = Invoke-RestMethod -Uri "https://api.github.com/repos/microsoft/vscode-js-debug/releases/latest" -Headers $headers
$jsAsset = $jsRelease.assets | Where-Object { $_.name -like "js-debug-dap-*.tar.gz" } | Select-Object -First 1
if (-not $jsAsset) { throw "The latest js-debug release did not provide its standalone DAP archive." }
$jsArchive = Join-Path $env:TEMP $jsAsset.name
Invoke-WebRequest -Uri $jsAsset.browser_download_url -OutFile $jsArchive -Headers @{ "User-Agent" = "Studio.exe" }
Remove-Item -Path (Join-Path $jsRoot "*") -Recurse -Force -ErrorAction SilentlyContinue
tar.exe -xzf $jsArchive -C $jsRoot
if ($LASTEXITCODE -ne 0) { throw "Could not extract the JavaScript DAP adapter." }
$jsEntry = Get-ChildItem -Path $jsRoot -Filter "dapDebugServer.js" -File -Recurse | Select-Object -First 1
if (-not $jsEntry) { throw "The JavaScript DAP archive did not contain dapDebugServer.js." }
Set-Content -Path (Join-Path $jsRoot "adapter-path.txt") -Value $jsEntry.FullName -Encoding utf8

Write-Host "[5/5] Installing Samsung netcoredbg for .NET..." -ForegroundColor Cyan
$netRelease = Invoke-RestMethod -Uri "https://api.github.com/repos/Samsung/netcoredbg/releases/latest" -Headers $headers
$netAsset = $netRelease.assets | Where-Object { $_.name -eq "netcoredbg-win64.zip" } | Select-Object -First 1
if (-not $netAsset) { throw "The latest netcoredbg release did not provide a Windows x64 archive." }
$netArchive = Join-Path $env:TEMP $netAsset.name
Invoke-WebRequest -Uri $netAsset.browser_download_url -OutFile $netArchive -Headers @{ "User-Agent" = "Studio.exe" }
Expand-Archive -Path $netArchive -DestinationPath $netcoreRoot -Force
$netEntry = Get-ChildItem -Path $netcoreRoot -Filter "netcoredbg.exe" -File -Recurse | Select-Object -First 1
if (-not $netEntry) { throw "The netcoredbg archive did not contain netcoredbg.exe." }
Set-Content -Path (Join-Path $netcoreRoot "adapter-path.txt") -Value $netEntry.FullName -Encoding utf8

Write-Host ""
Write-Host "Language servers and debug adapters are installed for this Windows user." -ForegroundColor Green
Write-Host "Restart Studio.exe so the updated PATH and adapter paths are detected." -ForegroundColor Green
