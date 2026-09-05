# Runs the ECS checks in Editor/Verify through Unity batch mode. No GUI, no human.
#
# Usage:   powershell -File verify.ps1
#
# Exit codes: 0 = all checks passed, 1 = a check failed, 2 = the editor is open (close it first).
#
# Second gate of the verification ladder (Editor/Docs/workflow.md):
#   logictest.ps1  ->  build.ps1  ->  verify.ps1  ->  a human loads a test world once
#
# Same shape as build.ps1 on purpose, including the lockfile check and Start-Process: Unity.exe is
# a GUI-subsystem executable, so a plain call operator returns immediately and never sets
# $LASTEXITCODE.

param(
    [string]$Unity       = "C:\Program Files\Unity\Hub\Editor\6000.0.59f2\Editor\Unity.exe",
    [string]$ProjectPath = "C:\Unity\CoreKeeper",
    [string]$LogFile     = (Join-Path $env:TEMP "nbz_verify.log")
)

$ErrorActionPreference = "Stop"
$method = "NoBreakZone.EditorTools.CliVerify.All"

if (-not (Test-Path $Unity))       { Write-Host "Unity not found: $Unity"; exit 1 }
if (-not (Test-Path $ProjectPath)) { Write-Host "Project not found: $ProjectPath"; exit 1 }

$lock = Join-Path $ProjectPath "Temp\UnityLockfile"
if (Test-Path $lock) {
    Write-Host "Unity lockfile present: $lock"
    Write-Host "Close the Unity editor for this project, then rerun. (exit 2)"
    exit 2
}

if (Test-Path $LogFile) { Remove-Item $LogFile -Force }

Write-Host "Verifying NoBreakZone systems"
Write-Host "(log: $LogFile)"

$unityArgs = @(
    "-batchmode", "-quit",
    "-projectPath", "`"$ProjectPath`"",
    "-executeMethod", $method,
    "-logFile", "`"$LogFile`""
)
$proc = Start-Process -FilePath $Unity -ArgumentList $unityArgs -Wait -PassThru
$code = $proc.ExitCode

if (Test-Path $LogFile) {
    Write-Host "----- checks -----"
    Select-String -Path $LogFile -Pattern "^\[NBZ\]" | ForEach-Object { $_.Line }
    Write-Host "------------------"
}

if ($code -eq 0) {
    Write-Host "VERIFY OK"
} else {
    Write-Host "VERIFY FAILED (Unity exit $code). Full log: $LogFile"
}

exit $code
