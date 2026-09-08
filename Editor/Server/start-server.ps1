# Starts the dedicated test server on a FRESH WORLD and leaves it running.
#
# Usage:   powershell -File D:\NoBreakZoneServer\start-server.ps1
#          powershell -File D:\NoBreakZoneServer\start-server.ps1 -KeepWorld
#
# This is the script to use when someone is going to stay connected. The earlier one
# (join-and-verify.ps1) kills the server when its timeout expires, which it once did while a player
# was standing in the world. Nothing here ever stops the server: Stop-Process -Name CoreKeeperServer.
#
# Join with the Game ID printed below. The mod's self test runs on its own once the map is streamed
# in and writes every verdict to the log, so a connected player never has to do anything but be
# there. It runs ONCE: the suite is destructive, so a second run would be judging terrain the first
# one destroyed. Rerun this script for another verdict — it hands the test a fresh world.
#
# WHY IT THROWS THE WORLD AWAY EVERY TIME. The self test is destructive by design: it blows up walls,
# digs holes and builds pylons. Until 2026-09-08 this all landed in one save that was reused run
# after run and session after session, and by then the world held 367 switched-on pylons — enough
# that release-on-switch-off could never pass, because switching one off left 366 covering the same
# wall. A test that runs on the wreckage of its previous run is not repeatable. Pass -KeepWorld when
# you deliberately want to look at what a previous run left behind.
#
# This file is a copy of the one in the repo at Assets/NoBreakZone/Editor/Server/. Edit it there.
# What this folder is and how to delete it: README-DELETE-ME.md next to this file.

param(
    [string]$Root = 'D:\NoBreakZoneServer',
    [switch]$KeepWorld
)

$ErrorActionPreference = "Stop"

# Fixed so it never changes between runs. 15-28 alphanumeric characters, no Y, y, x, 0 or O.
$gameId = 'NoBreakZoneTestServer1'

$exe    = Join-Path $Root 'server\CoreKeeperServer.exe'
$data   = Join-Path $Root 'data'
$log    = Join-Path $data 'selftest.log'
$config = Join-Path $data 'mods\NoBreakZone\General-selfTest.json'
$worlds = Join-Path $data 'worlds'

if (-not (Test-Path $exe)) { Write-Host "server not installed: $exe"; exit 2 }

Get-Process CoreKeeperServer -ErrorAction SilentlyContinue | Stop-Process -Force

if ($KeepWorld) {
    Write-Host "keeping the existing world (-KeepWorld)"
} elseif (Test-Path $worlds) {
    # Deleting saves, so the target is checked rather than trusted. Both conditions have to hold:
    # the folder resolves to somewhere under $Root, and it is the server's own worlds folder. A
    # mistyped -Root then deletes nothing instead of eating a real save.
    $resolvedWorlds = (Resolve-Path $worlds).Path
    $resolvedRoot   = (Resolve-Path $Root).Path

    if (-not $resolvedWorlds.StartsWith($resolvedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        Write-Host "refusing to delete $resolvedWorlds — it is not under $resolvedRoot"
        exit 2
    }

    $removed = @(Get-ChildItem $resolvedWorlds -File -ErrorAction SilentlyContinue)
    foreach ($f in $removed) {
        Remove-Item $f.FullName -Force
        Write-Host "  discarded world file: $($f.Name)"
    }

    if ($removed.Count -eq 0) { Write-Host "no world to discard — starting on a new one anyway" }
}

if (Test-Path $config) {
    $json = Get-Content $config -Raw | ConvertFrom-Json
    if (-not $json.value) {
        $json.value = $true
        $json | ConvertTo-Json -Depth 5 | Set-Content $config -Encoding utf8
        Write-Host "selfTest switched on"
    }
} else {
    Write-Host "no config yet — this launch creates it, then rerun this script"
}

if (Test-Path $log) { Remove-Item $log -Force }

# -batchmode but NOT -nographics: part of world generation runs on the GPU (server\README.txt).
$serverArgs = @(
    '-batchmode',
    '-world', '0',
    '-worldname', 'NBZSelfTest',
    '-gameid', $gameId,
    '-datapath', "`"$data`"",
    '-maxplayers', '2',
    '-logfile', "`"$log`""
)

$proc = Start-Process -FilePath $exe -ArgumentList $serverArgs -PassThru

Write-Host ""
Write-Host "  server running (pid $($proc.Id)) — Stop-Process -Name CoreKeeperServer to stop it"
Write-Host ""
Write-Host "  Core Keeper -> Multiplayer -> join with Game ID:"
Write-Host ""
Write-Host "      $gameId"
Write-Host ""
Write-Host "  log: $log"
Write-Host ""
Write-Host "  a fresh world has to generate before anyone can join, so give it a moment"
Write-Host ""
