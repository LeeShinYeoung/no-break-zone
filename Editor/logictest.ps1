# Runs the offline logic checks in Editor/LogicTests~. Seconds, no Unity, no licence, no human.
#
# Usage:   powershell -File logictest.ps1
#
# Exit codes: 0 = all checks passed, 1 = a check failed, 2 = could not run.
#
# This is the first gate of the verification ladder (CLAUDE.md section 4):
#   logictest.ps1  ->  build.ps1  ->  verify.ps1  ->  a human loads a test world once
#
# WHY NOT THE UNITY TEST RUNNER: `Unity.exe -batchmode -runTests` is refused on this machine with
# "No valid Unity Editor license found" (exit 198), while `-batchmode -quit -executeMethod` works.
# So the pure logic is checked here instead, in a form a script can run.

param(
    [string]$Dotnet = "dotnet",
    [string]$Project = (Join-Path $PSScriptRoot "LogicTests~")
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $Project)) { Write-Host "Project not found: $Project"; exit 2 }

# Keep the restore inside this folder. Even with no PackageReference the SDK resolves framework
# reference packs through NuGet and would otherwise write them to %USERPROFILE%\.nuget\packages;
# pinning it here means deleting the repository takes the whole footprint with it.
$env:NUGET_PACKAGES = Join-Path $Project ".nuget"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"

Write-Host "Running NoBreakZone logic checks ($Project)"

# dotnet is a console executable, so & sets $LASTEXITCODE properly here (unlike Unity.exe in
# build.ps1, which is GUI-subsystem and needs Start-Process -Wait -PassThru).
& $Dotnet run --project $Project --nologo --verbosity quiet
$code = $LASTEXITCODE

if ($code -eq 0) {
    Write-Host "LOGIC OK"
} else {
    Write-Host "LOGIC FAILED (exit $code)"
}

exit $code
