[CmdletBinding()]
param()
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $root "artifacts\Studio\Studio.exe"
if (Test-Path $exe) { Start-Process -FilePath $exe; exit 0 }
$releaseExe = Join-Path $root "src\Studio.App\bin\Release\net10.0-windows\Studio.exe"
$debugExe = Join-Path $root "src\Studio.App\bin\Debug\net10.0-windows\Studio.exe"
$candidate = if (Test-Path $releaseExe) { $releaseExe } elseif (Test-Path $debugExe) { $debugExe } else { $null }
if ($candidate) { Start-Process -FilePath $candidate }
else {
    Write-Host "No build found. Building and launching..." -ForegroundColor Yellow
    dotnet run --project (Join-Path $root "src\Studio.App\Studio.App.csproj")
}
