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

if ($Uninstall) {
    sc.exe stop $ServiceName | Out-Null
    sc.exe delete $ServiceName
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
    Write-Error "error: $Exe not found - publish first (scripts/publish.sh, win-x64 row)"
    exit 1
}

# binPath= requires the space after '='; the apphost is the service binary.
sc.exe create $ServiceName binPath= "`"$Exe`"" start= auto DisplayName= "Zenkins Server"
sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/5000

# -Port lands through the per-service environment: the SCM reads this REG_MULTI_SZ and merges it
# into the service process environment at start. It must be written BEFORE the first start - a
# running service only re-reads it after the next restart.
New-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName" `
    -Name "Environment" -PropertyType MultiString `
    -Value @("ASPNETCORE_URLS=http://127.0.0.1:$Port") -Force | Out-Null

sc.exe start $ServiceName
Write-Host "installed: $ServiceName ($Exe, port $Port via service Environment) - data lives in $InstallDir\App_Data"
