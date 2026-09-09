# Builds the NoBreakZone mod via Unity batch mode and installs it straight into the
# game's local Mods folder — and into the dedicated test server's, when that exists.
# No editor GUI, no clicking.
#
# Usage:   powershell -File build.ps1
#          powershell -File build.ps1 -ExportPath "D:\...\StreamingAssets\Mods"
#          powershell -File build.ps1 -ServerPath ""     # skip the server copy
#
# WHY THE SERVER COPY IS HERE. The self test runs on the dedicated server, and the server loads the
# mod from its OWN StreamingAssets — a different folder from the game client's. Until 2026-09-09 this
# script only wrote to the client, so the server kept running whatever build was last copied there by
# hand. On 2026-09-08 that was a four-day-old build, and the mistake was caught with a human already
# connected and waiting. A verdict from stale code is worse than no verdict, so the copy belongs
# where the build is, not in somebody's memory.
#
# Exit codes: 0 = build OK (and installed everywhere it could be), 1 = build failed or the
#             server copy failed, 2 = editor is open (close it first).
#
# The Unity editor for this project MUST be closed while this runs: a running editor
# holds Temp/UnityLockfile and batch mode cannot open the project a second time.

param(
    [string]$Unity       = "C:\Program Files\Unity\Hub\Editor\6000.0.59f2\Editor\Unity.exe",
    [string]$ProjectPath = "C:\Unity\CoreKeeper",
    [string]$ExportPath  = "D:\SteamLibrary\steamapps\common\Core Keeper\CoreKeeper_Data\StreamingAssets\Mods",
    [string]$ServerPath  = "D:\NoBreakZoneServer\server\CoreKeeperServer_Data\StreamingAssets\Mods",
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

if ($code -ne 0) {
    Write-Host "BUILD FAILED (Unity exit $code). Full log: $LogFile"
    exit $code
}

Write-Host "BUILD OK  -> $ExportPath\NoBreakZone"

# The dedicated server only exists on the machine that set one up, so its absence is normal and
# silent-ish rather than an error. Mirroring (not copying) so a file the build stopped producing does
# not linger on the server and get loaded.
if ($ServerPath -and (Test-Path $ServerPath)) {
    $from = Join-Path $ExportPath "NoBreakZone"
    $to   = Join-Path $ServerPath "NoBreakZone"

    robocopy $from $to /MIR /NFL /NDL /NJH /NJS /NP | Out-Null

    # robocopy uses exit codes as a bit field: 0-7 are success (0 = nothing to do, 1 = files copied),
    # 8 and up are real failures. $LASTEXITCODE has to be read before anything else runs.
    $rc = $LASTEXITCODE
    if ($rc -lt 8) {
        Write-Host "SERVER OK -> $to"
    } else {
        Write-Host "SERVER COPY FAILED (robocopy $rc) -> $to"
        Write-Host "The build is installed for the game but the server still holds an older one."
        exit 1
    }
} elseif ($ServerPath) {
    Write-Host "(no dedicated server at $ServerPath — skipping that copy)"
}

exit 0
