param(
    [string]$Dotnet = $null
)

$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $scriptDir

# Auto-detect dotnet SDK executable if not specified or default has no SDK
if (-not $Dotnet) {
    $dotnetRootCandidate = if ($env:DOTNET_ROOT) { Join-Path $env:DOTNET_ROOT 'dotnet.exe' } else { $null }
    $candidates = @(
        'dotnet',
        (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'),
        $dotnetRootCandidate
    ) | Where-Object { 
        if (-not $_) { return $false }
        if ($_ -eq 'dotnet') { return $true }
        return (Test-Path -LiteralPath $_ -ErrorAction SilentlyContinue)
    }

    foreach ($candidate in $candidates) {
        try {
            $sdkList = & $candidate --list-sdks 2>$null
            if ($sdkList -and $sdkList.Count -gt 0) {
                $Dotnet = $candidate
                break
            }
        }
        catch {
        }
    }

    if (-not $Dotnet) {
        $Dotnet = 'dotnet'
    }
}

Push-Location $root
try {
    Write-Host "Using .NET: $Dotnet" -ForegroundColor Cyan
    & $Dotnet build (Join-Path $root 'ZenConnect.sln') --configuration Debug
    if ($LASTEXITCODE -ne 0) {
        throw 'Dotnet build failed.'
    }

    # Match the Dockerfile's server-only target even when verifying on Windows.
    # Restore explicitly: the desktop build above restores net8.0-windows assets.
    $serverProject = Join-Path $root 'src\ZenConnect\ZenConnect.csproj'
    $serverOutput = Join-Path $root 'artifacts\render-publish'
    & $Dotnet restore $serverProject -p:TargetFramework=net8.0
    if ($LASTEXITCODE -ne 0) {
        throw 'Server restore failed.'
    }
    & $Dotnet publish $serverProject --configuration Release -p:TargetFramework=net8.0 --no-restore --output $serverOutput
    if ($LASTEXITCODE -ne 0) {
        throw 'Server publish failed.'
    }

    $jsPath = Join-Path $root 'src\ZenConnect\wwwroot\app.js'
    if (Get-Command node -ErrorAction SilentlyContinue) {
        if (Test-Path -LiteralPath $jsPath) {
            & node --check $jsPath
            if ($LASTEXITCODE -ne 0) {
                throw 'JavaScript syntax check failed.'
            }
        }
    }

    $powerShellScripts = Get-ChildItem -LiteralPath $scriptDir -Filter '*.ps1' -File
    foreach ($script in $powerShellScripts) {
        $parseErrors = $null
        $tokens = $null
        [System.Management.Automation.Language.Parser]::ParseFile(
            $script.FullName,
            [ref]$tokens,
            [ref]$parseErrors) | Out-Null
        if ($parseErrors.Count -gt 0) {
            throw ("PowerShell syntax failed: {0}`n{1}" -f $script.FullName, ($parseErrors | ForEach-Object Message | Out-String))
        }
    }

    Write-Host 'ZenConnect verification passed.' -ForegroundColor Green
}
finally {
    Pop-Location
}
