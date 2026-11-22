[CmdletBinding()]
param(
    [switch]$Node22,
    [switch]$Python3,
    [switch]$DotNet10
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$transcriptStarted = $false
$work = $null
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

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $forwardArguments = @(
        '-NoLogo',
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', ('"{0}"' -f $PSCommandPath)
    )
    if ($Node22) { $forwardArguments += '-Node22' }
    if ($Python3) { $forwardArguments += '-Python3' }
    if ($DotNet10) { $forwardArguments += '-DotNet10' }

    try {
        $elevated = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -ArgumentList $forwardArguments -Verb RunAs -WindowStyle Normal -Wait -PassThru
        exit $elevated.ExitCode
    }
    catch {
        Write-Error "Administrator approval was not granted: $($_.Exception.Message)"
        exit 1
    }
}

$logDirectory = Join-Path $env:ProgramData 'Luis Apps Inc\Studio.exe\Logs'
New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
$logPath = Join-Path $logDirectory 'developer-tools-install.log'
$failures = [System.Collections.Generic.List[string]]::new()

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
            Write-Step 'Installing Node.js 22 (official signed Windows x64 MSI)'
            $releases = Invoke-RestMethod -Uri 'https://nodejs.org/dist/index.json'
            $release = $releases |
                Where-Object { $_.version -match '^v22\.' -and $_.files -contains 'win-x64-msi' } |
                Sort-Object { [version]$_.version.TrimStart('v') } -Descending |
                Select-Object -First 1
            if (-not $release) { throw 'Could not find an official Node.js 22 x64 MSI release.' }

            $msiName = "node-$($release.version)-x64.msi"
            $baseUrl = "https://nodejs.org/dist/$($release.version)"
            $msiPath = Join-Path $work $msiName
            $checksumPath = Join-Path $work 'SHASUMS256.txt'
            Invoke-OfficialDownload "$baseUrl/$msiName" $msiPath
            Invoke-OfficialDownload "$baseUrl/SHASUMS256.txt" $checksumPath

            $checksumLine = Get-Content $checksumPath |
                Where-Object { $_ -match ("\s" + [regex]::Escape($msiName) + '
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
                ("TargetDir=`"$pythonDirectory`""),
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
}) } |
                Select-Object -First 1
            if (-not $checksumLine) { throw 'The official Node.js checksum file does not contain the x64 MSI.' }
            $expected = ($checksumLine -split '\s+')[0]
            $actual = (Get-FileHash $msiPath -Algorithm SHA256).Hash
            if ($actual -ine $expected) { throw 'The Node.js MSI failed SHA-256 verification.' }

            $signature = Get-AuthenticodeSignature -FilePath $msiPath
            if ($signature.Status -ne 'Valid' -or
                $signature.SignerCertificate.Subject -notmatch 'OpenJS Foundation|Node\.js Foundation') {
                throw 'The Node.js MSI signature could not be verified as an official Node.js release.'
            }

            $nodeInstall = Start-Process -FilePath 'msiexec.exe' -ArgumentList @('/i', $msiPath, '/qn', '/norestart') -Wait -PassThru
            if ($nodeInstall.ExitCode -notin @(0, 3010)) {
                throw "Node.js MSI installer returned exit code $($nodeInstall.ExitCode)."
            }

            $nodeDirectory = Join-Path $env:ProgramFiles 'nodejs'
            $nodeExe = Join-Path $nodeDirectory 'node.exe'
            if (-not (Test-Path $nodeExe)) { throw 'Node.js was not found after the MSI installation.' }
            Add-MachinePath @($nodeDirectory)

            $nodeVersion = (& $nodeExe --version).Trim()
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
                ("TargetDir=`"$pythonDirectory`""),
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