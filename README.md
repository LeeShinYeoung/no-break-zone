# No Break Zone

A Core Keeper mod that stops your base from being destroyed by your own pickaxe,
explosions, and stray attacks — while leaving ore, walls, and crops fully mineable.

> **Status: in development.** Damage blocking works and is verified in game, but it
> currently applies to *every* placeable object. The Pylon that scopes protection to an
> area is not built yet. Not published to mod.io or the Workshop.

## Install

Copy the built `NoBreakZone` folder into:

```
<Core Keeper>/CoreKeeper_Data/StreamingAssets/Mods/
```

## Development setup

This repository is the **mod folder only**, not a full Unity project. The Unity project is
the Mod SDK; this repo is checked out inside it. To reconstruct a working environment:

1. Clone the SDK — it is the Unity project:
   ```
   git clone https://github.com/Pugstorm/CoreKeeperModSDK
   ```
2. Clone this repository into the SDK's `Assets/` folder, as `NoBreakZone`:
   ```
   git clone https://github.com/LeeShinYeoung/no-break-zone CoreKeeperModSDK/Assets/NoBreakZone
   ```
3. Add the SDK folder as a project in Unity Hub and open it. When Unity Hub installs the
   editor, enable **Linux Build Support (Mono)** — without it the mod cannot be built.
   Optionally add `-disable-assembly-updater` to the project's command line arguments.
4. Point [Editor/build.ps1](Editor/build.ps1) at your machine: its `-Unity`, `-ProjectPath`
   and `-ExportPath` defaults are hardcoded to one developer's paths.

> **Unity version.** The SDK README specifies `6000.0.58f2`; `build.ps1` currently defaults
> to `6000.0.59f2`. Match whichever the SDK asks for if the two disagree.

> **Not in this repository.** The `ModBuilderSettings` asset that drives the build
> (mod name, dependencies, `modPath`, Linux build flag) currently lives outside the mod
> folder and is therefore untracked. See `Editor/Docs/status.md`.

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
| `Scripts/` | runtime systems |
| `Data/` | runtime mod data asset |
| `Editor/` | build tooling and docs — excluded from the mod bundle |
| `Editor/Docs/` | design, workflow, status, research |
| `Editor/GameData/` | full object-database dump used to derive the protection rules |
| `.claude/` | agent harness (see [CLAUDE.md](CLAUDE.md)) |

Project documentation in `Editor/Docs/` is written in Korean.

## License

MIT — see [LICENSE.md](LICENSE.md).
