# No Break Zone

A Core Keeper mod that stops your base from being destroyed by your own pickaxe,
explosions, and stray attacks — while leaving ore, walls, and crops fully mineable.

> **Status: in development.** Damage blocking works and is verified in game, but it
> currently applies to *every* placeable object. The Pylon that scopes protection to an
> area is not built yet. Not published to mod.io or the Workshop.

## Requirements

- Core Keeper
- Unity 6000.0.59f2 with **Linux Build Support (Mono)**, plus the
  [Core Keeper Mod SDK](https://modding.corekeepergame.com)

## Install

Copy the built `NoBreakZone` folder into:

```
<Core Keeper>/CoreKeeper_Data/StreamingAssets/Mods/
```

## Build

Windows only — the build drives the Unity editor in batch mode.

```powershell
# close the Unity editor first: batch mode cannot take the project lock twice
powershell -File Editor/build.ps1
```

Exit codes: `0` built, `1` failed, `2` the editor is still open.
The mod is installed straight into the game's `Mods` folder.

## Layout

The repository root *is* the mod folder (`Assets/NoBreakZone/` in the Unity project), so
everything outside `Editor/` ships inside the mod bundle. Development docs and draft art
live under `Editor/` for that reason.

| Path | |
| --- | --- |
| `NoBreakZone*.cs` | runtime systems |
| `Editor/` | build tooling and docs — excluded from the mod bundle |
| `Editor/Docs/` | design, workflow, status, research |
| `Editor/GameData/` | full object-database dump used to derive the protection rules |
| `.claude/` | agent harness (see [CLAUDE.md](CLAUDE.md)) |

Project documentation in `Editor/Docs/` is written in Korean.

## License

MIT — see [LICENSE.md](LICENSE.md).
