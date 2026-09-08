param(
    [switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'

if ($env:ZENCONNECT_VALIDATE_ONLY -eq '1') {
    $ValidateOnly = $true
}

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $scriptDir
$projectDir = Join-Path $root 'src\ZenConnect'
$projectFile = Join-Path $projectDir 'ZenConnect.csproj'
$publishDir = Join-Path $projectDir 'bin\Release\net8.0-windows\win-x64\publish'
$clientExe = Join-Path $publishDir 'ZenConnect.exe'
$publishStamp = Join-Path $publishDir '.zenconnect-publish.stamp'
$localUrl = 'http://localhost:5080'
$settingsDir = Join-Path $env:LOCALAPPDATA 'ZenConnect'
$logDir = Join-Path $settingsDir 'logs'
$runLog = Join-Path $logDir 'launcher.log'

New-Item -ItemType Directory -Path $logDir -Force | Out-Null

function Write-RunLog([string]$message) {
    $line = '{0:yyyy-MM-dd HH:mm:ss.fff} | {1}' -f (Get-Date), $message
    Add-Content -LiteralPath $runLog -Value $line -Encoding UTF8
}

function Show-FatalError([string]$message) {
    Write-RunLog ('ERROR | ' + $message)
    try {
        Add-Type -AssemblyName System.Windows.Forms
        [System.Windows.Forms.MessageBox]::Show(
            $message + "`r`n`r`nChi tiet: " + $runLog,
            'ZenConnect khong the khoi dong',
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Error) | Out-Null
    }
    catch {
    }
}

function Find-DotnetSdk {
    $dotnetRootCandidate = if ($env:DOTNET_ROOT) { Join-Path $env:DOTNET_ROOT 'dotnet.exe' } else { $null }
    $candidates = @(
        'dotnet',
        (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'),
        $dotnetRootCandidate
    ) | Where-Object { $_ }

    foreach ($candidate in $candidates) {
        try {
            $sdks = @(& $candidate --list-sdks 2>$null)
            if ($LASTEXITCODE -eq 0 -and $sdks.Count -gt 0) {
                return $candidate
            }
        }
        catch {
        }
    }

    return $null
}

function Get-LatestSourceWriteTime([string]$directory) {
    $latest = [DateTime]::MinValue
    if (-not (Test-Path -LiteralPath $directory)) {
        return $latest
    }

    $files = Get-ChildItem -LiteralPath $directory -Recurse -File | Where-Object {
        $_.FullName -notmatch '\\(bin|obj|App_Data)\\'
    }
    foreach ($file in $files) {
        if ($file.LastWriteTimeUtc -gt $latest) {
            $latest = $file.LastWriteTimeUtc
        }
    }
    return $latest
}

function Test-PublishRequired([string]$sourceDir, [string]$artifact, [string]$stamp) {
    if (-not (Test-Path -LiteralPath $artifact)) {
        return $true
    }
    if (-not (Test-Path -LiteralPath $stamp)) {
        return $true
    }

    $latestSource = Get-LatestSourceWriteTime $sourceDir
    return (Get-Item -LiteralPath $stamp).LastWriteTimeUtc -lt $latestSource
}

function Set-PublishStamp([string]$path) {
    [IO.File]::WriteAllText($path, [DateTimeOffset]::UtcNow.ToString('O'))
}

function Stop-StaleServerPort {
    for ($check = 0; $check -lt 20; $check++) {
        $listeners = @(Get-NetTCPConnection -LocalPort 5080 -State Listen -ErrorAction SilentlyContinue |
            Sort-Object OwningProcess -Unique)
        if ($listeners.Count -eq 0) {
            return
        }

        foreach ($listener in $listeners) {
            $proc = Get-Process -Id $listener.OwningProcess -ErrorAction SilentlyContinue
            if (-not $proc) {
                continue
            }
            if ($proc.ProcessName -match 'ZenConnect|ScreenReviewServer') {
                Write-RunLog ("Stopping stale listener PID $($proc.Id): $($proc.ProcessName)")
                Stop-Process -Id $proc.Id -Force
                Wait-Process -Id $proc.Id -Timeout 5 -ErrorAction SilentlyContinue
            }
        }
        Start-Sleep -Milliseconds 100
    }
}

try {
    Write-RunLog 'Starting ZenConnect single-project bootstrap.'
    Stop-StaleServerPort

    $needsPublish = Test-PublishRequired $projectDir $clientExe $publishStamp
    if ($needsPublish) {
        $dotnet = Find-DotnetSdk
        if (-not $dotnet) {
            throw 'Source moi hon ban chay nhung khong tim thay .NET 8 SDK de build/publish.'
        }

        Write-RunLog 'Publishing ZenConnect single project...'
        $publishArgs = @(
            'publish', $projectFile,
            '--configuration', 'Release',
            '--runtime', 'win-x64',
            '--self-contained', 'true',
            '-p:PublishSingleFile=true',
            '--nologo'
        )
        $output = @(& $dotnet @publishArgs 2>&1)
        foreach ($line in $output) {
            Write-RunLog ([string]$line)
        }
        if ($LASTEXITCODE -ne 0) {
            throw 'Publish ZenConnect that bai.'
        }
        Set-PublishStamp $publishStamp
    }

    if (-not (Test-Path -LiteralPath $clientExe)) {
        throw 'Khong tim thay ZenConnect.exe sau khi publish.'
    }

    if ($ValidateOnly) {
        Write-RunLog 'Validation-only check completed successfully.'
        return
    }

    Write-RunLog "Launching ZenConnect: $clientExe"
    $appProcess = Start-Process -FilePath $clientExe -WorkingDirectory $publishDir -PassThru
    Write-RunLog ("ZenConnect started; PID {0}." -f $appProcess.Id)
    $appProcess.WaitForExit()
    Write-RunLog ("ZenConnect exited with code {0}." -f $appProcess.ExitCode)
}
catch {
    Show-FatalError $_.Exception.Message
    exit 1
}
