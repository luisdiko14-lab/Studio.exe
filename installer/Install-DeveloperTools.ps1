[CmdletBinding()]
param(
    [switch]$Node22,
    [switch]$Python3,
    [switch]$DotNet10
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$logDirectory = Join-Path $env:ProgramData 'Luis Apps Inc\Studio.exe\Logs'
New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
$logPath = Join-Path $logDirectory 'developer-tools-install.log'
$transcriptStarted = $false
$failures = [System.Collections.Generic.List[string]]::new()

function Write-Step([string]$Message) {
    Write-Host ''
    Write-Host ('==> ' + $Message) -ForegroundColor Cyan
}

function Invoke-OfficialDownload([string]$Uri, [string]$Destination) {
    Write-Host "Downloading $Uri"
    Invoke-WebRequest -Uri $Uri -OutFile $Destination -UseBasicParsing
    if (-not (Test-Path $Destination) -or (Get-Item $Destination).Length -lt 1024) {
        throw "The download from $Uri was empty or incomplete."
    }
}

function Add-MachinePath([string[]]$Paths) {
    $current = [Environment]::GetEnvironmentVariable('Path', 'Machine')
    $entries = [System.Collections.Generic.List[string]]::new()
    if ($current) {
        foreach ($entry in ($current -split ';')) {
            if (-not [string]::IsNullOrWhiteSpace($entry) -and
                -not ($entries | Where-Object { $_.TrimEnd('\') -ieq $entry.TrimEnd('\') })) {
                $entries.Add($entry)
            }
        }
    }

    foreach ($path in $Paths) {
        if (Test-Path $path) {
            $alreadyPresent = $entries | Where-Object { $_.TrimEnd('\') -ieq $path.TrimEnd('\') }
            if (-not $alreadyPresent) { $entries.Add($path) }
        }
    }

    [Environment]::SetEnvironmentVariable('Path', ($entries -join ';'), 'Machine')
    foreach ($path in $Paths) {
        if ((Test-Path $path) -and (($env:Path -split ';') -notcontains $path)) {
            $env:Path = $path + ';' + $env:Path
        }
    }
}

try {
    Start-Transcript -Path $logPath -Append | Out-Null
    $transcriptStarted = $true

    if (-not ($Node22 -or $Python3 -or $DotNet10)) {
        throw 'No developer tools were selected.'
    }

    $work = Join-Path $env:TEMP ('StudioTools-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $work -Force | Out-Null

    if ($Node22) {
        try {
            Write-Step 'Installing Node.js 22 (official Windows x64 ZIP)'
            $releases = Invoke-RestMethod -Uri 'https://nodejs.org/dist/index.json'
            $release = $releases |
                Where-Object { $_.version -match '^v22\.' -and $_.files -contains 'win-x64-zip' } |
                Sort-Object { [version]$_.version.TrimStart('v') } -Descending |
                Select-Object -First 1
            if (-not $release) { throw 'Could not find an official Node.js 22 x64 release.' }

            $archiveName = "node-$($release.version)-win-x64.zip"
            $baseUrl = "https://nodejs.org/dist/$($release.version)"
            $archivePath = Join-Path $work $archiveName
            $checksumPath = Join-Path $work 'SHASUMS256.txt'
            Invoke-OfficialDownload "$baseUrl/$archiveName" $archivePath
            Invoke-OfficialDownload "$baseUrl/SHASUMS256.txt" $checksumPath

            $checksumLine = Get-Content $checksumPath |
                Where-Object { $_ -match ("\s" + [regex]::Escape($archiveName) + '$') } |
                Select-Object -First 1
            if (-not $checksumLine) { throw 'The official Node.js checksum file does not contain the x64 ZIP.' }
            $expected = ($checksumLine -split '\s+')[0]
            $actual = (Get-FileHash $archivePath -Algorithm SHA256).Hash
            if ($actual -ine $expected) { throw 'The Node.js download failed SHA-256 verification.' }

            $extract = Join-Path $work 'node-extracted'
            Expand-Archive -LiteralPath $archivePath -DestinationPath $extract -Force
            $source = Join-Path $extract "node-$($release.version)-win-x64"
            if (-not (Test-Path (Join-Path $source 'node.exe'))) {
                throw 'The official Node.js archive did not contain node.exe.'
            }

            $destination = Join-Path $env:ProgramFiles 'nodejs'
            New-Item -ItemType Directory -Path $destination -Force | Out-Null
            Get-ChildItem -LiteralPath $destination -Force | Remove-Item -Recurse -Force
            Copy-Item -Path (Join-Path $source '*') -Destination $destination -Recurse -Force
            Add-MachinePath @($destination)

            $nodeVersion = (& (Join-Path $destination 'node.exe') --version).Trim()
            if ($LASTEXITCODE -ne 0 -or $nodeVersion -notmatch '^v22\.') {
                throw "Node.js verification failed. Reported version: $nodeVersion"
            }
            Write-Host "Installed Node.js $nodeVersion."
        }
        catch {
            $failures.Add("Node.js 22: $($_.Exception.Message)")
            Write-Host "FAILED: $($_.Exception.Message)" -ForegroundColor Red
        }
    }

    if ($Python3) {
        try {
            Write-Step 'Installing Python 3 (official 64-bit installer)'
            $pythonVersion = '3.15.1'
            $pythonInstaller = Join-Path $work "python-$pythonVersion-amd64.exe"
            $pythonUrl = "https://www.python.org/ftp/python/$pythonVersion/python-$pythonVersion-amd64.exe"
            Invoke-OfficialDownload $pythonUrl $pythonInstaller

            $signature = Get-AuthenticodeSignature -FilePath $pythonInstaller
            if ($signature.Status -ne 'Valid' -or
                $signature.SignerCertificate.Subject -notmatch 'Python Software Foundation') {
                throw 'The Python installer signature could not be verified as a Python Software Foundation release.'
            }

            $pythonDirectory = Join-Path $env:ProgramFiles 'Python315'
            $pythonArgs = @(
                '/quiet',
                'InstallAllUsers=1',
                "TargetDir=$pythonDirectory",
                'PrependPath=1',
                'Include_pip=1',
                'Include_test=0',
                'Shortcuts=0'
            )
            $pythonProcess = Start-Process -FilePath $pythonInstaller -ArgumentList $pythonArgs -Wait -PassThru
            if ($pythonProcess.ExitCode -notin @(0, 3010)) {
                throw "The Python installer returned exit code $($pythonProcess.ExitCode)."
            }

            Add-MachinePath @($pythonDirectory, (Join-Path $pythonDirectory 'Scripts'))
            $pythonExe = Join-Path $pythonDirectory 'python.exe'
            if (-not (Test-Path $pythonExe)) { throw 'Python was not found after installation.' }
            $reportedPython = (& $pythonExe --version 2>&1 | Out-String).Trim()
            if ($LASTEXITCODE -ne 0 -or $reportedPython -notmatch '^Python 3\.') {
                throw "Python verification failed. Reported version: $reportedPython"
            }
            Write-Host "Installed $reportedPython."
        }
        catch {
            $failures.Add("Python 3: $($_.Exception.Message)")
            Write-Host "FAILED: $($_.Exception.Message)" -ForegroundColor Red
        }
    }

    if ($DotNet10) {
        try {
            Write-Step 'Installing the .NET 10 SDK (official Microsoft installer script)'
            $dotnetScript = Join-Path $work 'dotnet-install.ps1'
            Invoke-OfficialDownload 'https://dot.net/v1/dotnet-install.ps1' $dotnetScript

            $dotnetDirectory = Join-Path $env:ProgramFiles 'dotnet'
            & $dotnetScript -Channel '10.0' -Architecture 'x64' -InstallDir $dotnetDirectory -NoPath
            if ($LASTEXITCODE -ne 0) {
                throw ".NET SDK installation script returned exit code $LASTEXITCODE."
            }

            [Environment]::SetEnvironmentVariable('DOTNET_ROOT', $dotnetDirectory, 'Machine')
            Add-MachinePath @($dotnetDirectory)
            $dotnetExe = Join-Path $dotnetDirectory 'dotnet.exe'
            if (-not (Test-Path $dotnetExe)) { throw '.NET SDK dotnet.exe was not found after installation.' }
            $sdkList = (& $dotnetExe --list-sdks 2>&1 | Out-String)
            if ($LASTEXITCODE -ne 0 -or $sdkList -notmatch '(?m)^10\.') {
                throw '.NET 10 SDK verification failed; dotnet --list-sdks did not report a 10.x SDK.'
            }
            Write-Host 'Installed .NET 10 SDK.'
            Write-Host $sdkList.Trim()
        }
        catch {
            $failures.Add(".NET 10 SDK: $($_.Exception.Message)")
            Write-Host "FAILED: $($_.Exception.Message)" -ForegroundColor Red
        }
    }

    if ($failures.Count -gt 0) {
        Write-Host ''
        Write-Host 'One or more selected tools could not be installed:' -ForegroundColor Red
        foreach ($failure in $failures) { Write-Host " - $failure" -ForegroundColor Red }
        Write-Host "Detailed log: $logPath"
        exit 1
    }

    Write-Step 'All selected developer tools installed successfully'
    Write-Host "Log: $logPath"
    exit 0
}
catch {
    Write-Host "Installation failed: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "Log: $logPath"
    exit 1
}
finally {
    if ($transcriptStarted) {
        try { Stop-Transcript | Out-Null } catch { }
    }
    if ($work -and (Test-Path $work)) {
        try { Remove-Item $work -Recurse -Force } catch { }
    }
}