# Builds the NoBreakZone mod via Unity batch mode and installs it straight into the
# game's local Mods folder. No editor GUI, no clicking.
#
# Usage:   powershell -File build.ps1
#          powershell -File build.ps1 -ExportPath "D:\...\StreamingAssets\Mods"
#
# Exit codes: 0 = build OK, 1 = build failed, 2 = editor is open (close it first).
#
# The Unity editor for this project MUST be closed while this runs: a running editor
# holds Temp/UnityLockfile and batch mode cannot open the project a second time.

param(
    [string]$Unity       = "C:\Program Files\Unity\Hub\Editor\6000.0.59f2\Editor\Unity.exe",
    [string]$ProjectPath = "C:\Unity\CoreKeeper",
    [string]$ExportPath  = "D:\SteamLibrary\steamapps\common\Core Keeper\CoreKeeper_Data\StreamingAssets\Mods",
    [string]$LogFile     = (Join-Path $env:TEMP "nbz_build.log")
)

$ErrorActionPreference = "Stop"
$method = "NoBreakZone.EditorTools.CliBuild.BuildToGame"

if (-not (Test-Path $Unity))       { Write-Host "Unity not found: $Unity"; exit 1 }
if (-not (Test-Path $ProjectPath)) { Write-Host "Project not found: $ProjectPath"; exit 1 }

# Refuse to run while the editor holds the project lock.
$lock = Join-Path $ProjectPath "Temp\UnityLockfile"
if (Test-Path $lock) {
    Write-Host "Unity lockfile present: $lock"
    Write-Host "Close the Unity editor for this project, then rerun. (exit 2)"
    exit 2
}

if (Test-Path $LogFile) { Remove-Item $LogFile -Force }

Write-Host "Building NoBreakZone -> $ExportPath"
Write-Host "(log: $LogFile)"

# Unity.exe is a GUI-subsystem executable, so a plain call operator (&) returns
# immediately without waiting and never sets $LASTEXITCODE. Use Start-Process -Wait
# and read the real exit code from the process object. Space-containing paths are
# quoted per-argument because PS 5.1 Start-Process does not auto-quote ArgumentList.
$unityArgs = @(
    "-batchmode", "-quit",
    "-projectPath", "`"$ProjectPath`"",
    "-executeMethod", $method,
    "-nbzExportPath", "`"$ExportPath`"",
    "-logFile", "`"$LogFile`""
)
$proc = Start-Process -FilePath $Unity -ArgumentList $unityArgs -Wait -PassThru
$code = $proc.ExitCode

if (Test-Path $LogFile) {
    Write-Host "----- build log tail -----"
    Get-Content $LogFile -Tail 40
    Write-Host "--------------------------"
}

if ($code -eq 0) {
    Write-Host "BUILD OK  -> $ExportPath\NoBreakZone"
} else {
    Write-Host "BUILD FAILED (Unity exit $code). Full log: $LogFile"
}
exit $code
