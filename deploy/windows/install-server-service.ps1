# I-003: install/uninstall the published Zenkins Server as a Windows Service.
#
#   .\install-server-service.ps1 -InstallDir C:\zenkins\server -Port 5000
#   .\install-server-service.ps1 -Uninstall
#
# The app is a plain console binary for the Service Control Manager: UseWindowsService() in the
# hosting layer detects the service context and swaps in the Windows service lifetime (it does not
# pin the content root). Configuration goes through environment variables, set per-service via the
# SCM's registry value HKLM\SYSTEM\CurrentControlSet\Services\<name>\Environment (REG_MULTI_SZ;
# the SCM merges each KEY=VALUE line into the service process environment when it starts the
# service). Do NOT rely on appsettings.json here: a service's working directory is
# C:\Windows\System32, so file discovery there is unreliable - put every setting in the service
# Environment (ASPNETCORE_URLS, Database__Provider, ConnectionStrings__Default,
# Database__ApplyMigrationsOnStartup, ...) instead. Every persistent path (App_Data: database,
# key ring, artifact store) resolves from the deployment directory via AppContext.BaseDirectory,
# wherever the service starts.
#
# Database__ApplyMigrationsOnStartup=true matches the launchd plist and the systemd unit: a
# single-node self-host applies pending EF Core migrations at startup (Production would otherwise
# skip them - Program.cs defaults the flag to Development-only - and a fresh deployment's first
# /account/register would fail with 'no such table: BootstrapMarkers'). Multi-node deployments
# that migrate once per deployment remove this line instead.
#
# sc.exe failure actions are the restart-on-crash story; StartType Automatic is start-on-boot.
#
# NOTE (card I-003): not executed on the machine at hand (macOS); the script ships as packaging for
# DOC-003 to document and verify on a Windows host. Run from an elevated PowerShell.
param(
    [string]$InstallDir,
    [int]$Port = 5000,
    [switch]$Uninstall
)

$ServiceName = "ZenkinsServer"

# Q-007: every sc.exe call is checked. The earlier version ignored $LASTEXITCODE, so a second
# install over an existing service (sc.exe create answers 1073 'already exists') still printed
# 'installed' and exited 0 - a reinstall onto a different directory looked successful while the
# old binPath stayed in the SCM. A failed step now stops the script with a non-zero exit code.
function Invoke-Sc {
    param([string[]]$ScArguments, [string]$Step)

    $output = & sc.exe @ScArguments 2>&1
    $code = $LASTEXITCODE
    if ($code -ne 0) {
        Write-Error "error: sc.exe $($ScArguments -join ' ') failed with exit code $code ($Step). Last output: $($output -join ' ')"
        exit 1
    }

    return $output
}

if ($Uninstall) {
    # A service that is already stopped answers 1062 here; that is not an uninstall failure.
    & sc.exe stop $ServiceName 2>&1 | Out-Null
    Invoke-Sc @('delete', $ServiceName) -Step 'deleting the service registration' | Out-Null
    # The per-service environment lives in the registry; remove it with the service.
    Remove-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName" `
        -Name "Environment" -ErrorAction SilentlyContinue
    Write-Host "uninstalled: $ServiceName (the deployment directory and its App_Data were left in place)"
    exit 0
}

if (-not $InstallDir) {
    Write-Error "usage: install-server-service.ps1 -InstallDir <dir> [-Port 5000] | -Uninstall"
    exit 1
}

$Exe = Join-Path $InstallDir "Zenkins.Server.exe"
if (-not (Test-Path $Exe)) {
    Write-Error "error: $Exe not found - publish first (scripts/publish.py, win-x64 row)"
    exit 1
}

# binPath= requires the space after '='; the apphost is the service binary.
Invoke-Sc @('create', $ServiceName, 'binPath=', "`"$Exe`"", 'start=', 'auto', 'DisplayName=', 'Zenkins Server') `
    -Step 'creating the service' | Out-Null
Invoke-Sc @('failure', $ServiceName, 'reset=', '86400', 'actions=', 'restart/5000/restart/5000/restart/5000') `
    -Step 'configuring restart-on-crash' | Out-Null

# -Port lands through the per-service environment: the SCM reads this REG_MULTI_SZ and merges it
# into the service process environment at start. It must be written BEFORE the first start - a
# running service only re-reads it after the next restart.
New-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName" `
    -Name "Environment" -PropertyType MultiString `
    -Value @(
        "ASPNETCORE_URLS=http://127.0.0.1:$Port",
        # Same semantics as the launchd plist and the systemd unit (see the header above).
        "Database__ApplyMigrationsOnStartup=true"
    ) -Force | Out-Null

Invoke-Sc @('start', $ServiceName) -Step 'starting the service' | Out-Null
Write-Host "installed: $ServiceName ($Exe, port $Port via service Environment) - data lives in $InstallDir\App_Data"
