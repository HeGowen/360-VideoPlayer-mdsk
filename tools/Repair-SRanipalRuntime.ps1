param(
    [switch]$NoPause
)

$ErrorActionPreference = "Continue"

function Test-Admin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Admin)) {
    $args = "-ExecutionPolicy Bypass -NoProfile -File `"$PSCommandPath`""
    if ($NoPause) {
        $args += " -NoPause"
    }
    Start-Process -FilePath "powershell.exe" -ArgumentList $args -Verb RunAs
    exit
}

Write-Host "Repairing SRanipal / Tobii / VIVE runtime..."

$processNames = @(
    "MDSK_360_VideoPlayer",
    "EyeCalibration",
    "EyeCalibrationDashboard",
    "sr_runtime"
)

foreach ($name in $processNames) {
    Get-Process -Name $name -ErrorAction SilentlyContinue | ForEach-Object {
        Write-Host "Stopping process $($_.ProcessName) PID=$($_.Id)"
        Stop-Process -Id $_.Id -Force -ErrorAction Continue
    }
}

$services = @(
    "VIVE Runtime Service",
    "Tobii VRU02 Runtime"
)

foreach ($service in $services) {
    $svc = Get-Service -Name $service -ErrorAction SilentlyContinue
    if ($null -ne $svc) {
        Write-Host "Stopping service $service"
        Stop-Service -Name $service -Force -ErrorAction Continue
    }
}

Start-Sleep -Seconds 5

$servicesToStart = $services.Clone()
[array]::Reverse($servicesToStart)
foreach ($service in $servicesToStart) {
    $svc = Get-Service -Name $service -ErrorAction SilentlyContinue
    if ($null -ne $svc) {
        Write-Host "Starting service $service"
        Start-Service -Name $service -ErrorAction Continue
    }
}

Start-Sleep -Seconds 5

$runtimeCandidates = @(
    "C:\Program Files (x86)\VIVE\Updater\App\SRanipal\sr_runtime.exe",
    "C:\Program Files\VIVE\SRanipal\sr_runtime.exe"
)

if (-not (Get-Process -Name "sr_runtime" -ErrorAction SilentlyContinue)) {
    foreach ($runtime in $runtimeCandidates) {
        if (Test-Path -LiteralPath $runtime) {
            Write-Host "Launching $runtime"
            Start-Process -FilePath $runtime
            break
        }
    }
}

Start-Sleep -Seconds 3

Write-Host ""
Write-Host "Current runtime status:"
Get-Process -Name "sr_runtime" -ErrorAction SilentlyContinue | Select-Object ProcessName,Id,Responding,StartTime
Get-Service -Name "VIVE Runtime Service","Tobii VRU02 Runtime" -ErrorAction SilentlyContinue | Select-Object Name,Status

Write-Host ""
Write-Host "If sr_runtime is still not responding or the tray icon stays red, reboot Windows."
if (-not $NoPause) {
    Read-Host "Press Enter to close"
}
