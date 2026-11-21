[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")][string]$Configuration = "Release",
    [switch]$Publish,
    [switch]$SelfContained
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Push-Location $root
try {
    & "$root\setup.ps1"
    if ($LASTEXITCODE -ne 0) { throw "Setup checks failed." }
    dotnet restore "$root\Studio.sln"
    if ($LASTEXITCODE -ne 0) { throw "NuGet restore failed." }
    dotnet build "$root\Studio.sln" -c $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Build failed." }
    if ($Publish) {
        $selfContainedValue = if ($SelfContained) { "true" } else { "false" }
        dotnet publish "$root\src\Studio.App\Studio.App.csproj" -c $Configuration -r win-x64 --self-contained $selfContainedValue -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "$root\artifacts\Studio"
        if ($LASTEXITCODE -ne 0) { throw "Publish failed." }
        Write-Host "Published application: $root\artifacts\Studio\Studio.exe" -ForegroundColor Green
    }
}
finally { Pop-Location }
