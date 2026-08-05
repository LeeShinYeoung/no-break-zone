# No Break Zone

A Core Keeper mod that stops your base from being destroyed by your own pickaxe,
explosions, and stray attacks — while leaving ore, walls, and crops fully mineable.

> **Status: in development, and largely unverified.** Damage blocking was confirmed in game
> at the point where it applied to every placeable in the world. Everything since — the
> pylon, its on/off switch, the workbench, the lens, the remote, and the settings — is
> written but has never been built or played. Not published to mod.io or the Workshop.

## What it adds

Protection is not global: it comes from a pylon you place and switch on, and it covers a
square around that pylon. Switch the pylon off and your base is ordinary again, so you can
remodel it.

| | |
| --- | --- |
| **No Break Pylon** | Placeable. Press **E** to switch on or off; the state survives save and load. While it is on, nothing inside its square can be destroyed — and neither can the pylon. |
| **Pylon Workbench** | Where the three below are made. Itself crafted at an iron workbench. |
| **Pylon Lens** | Hold it to see the edge of every switched-on pylon's square. Nothing else ever draws it. |
| **Pylon Remote** | Right-click a pylon from a distance to switch it. For when you have walled yourself out of reach of one. |

Ore, walls, pots and crops stay fully mineable and harvestable inside a protected area —
that is the one thing the protection rules will not do, because duplicating resources would
break a save permanently.

## Settings

Registered under `NoBreakZone` / `General`; the game decides where the file lives.

| Key | Default | |
| --- | --- | --- |
| `protectionDiameter` | `21` | Width and height in tiles of the square one pylon covers. The pylon stands in the middle, so an even number rounds down. |
| `blockMobDamage` | `true` | Stop every source of damage. Turn off to stop only what the player does, leaving mobs and explosions able to destroy protected objects. |
| `showRangeWithLens` | `true` | Draw the outline while the lens is held. |
| `remoteReachTiles` | `30` | How far the remote reaches. |

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
| `Scripts/` | runtime systems, graphics components and converters |
| `Prefabs/` · `Textures/` · `Data/` | the four objects — **generated**, see below |
| `Editor/genassets.py` | writes every prefab, sprite asset, text block and texture import from one spec list |
| `Editor/preflight.py` | static checks that run without Unity |
| `Editor/` | build tooling and docs — excluded from the mod bundle |
| `Editor/Docs/` | design, workflow, status, research |
| `Editor/GameData/` | full object-database dump used to derive the protection rules |
| `.claude/` | agent harness (see [CLAUDE.md](CLAUDE.md)) |

### Generated assets

Everything under `Prefabs/`, `Textures/` and `Data/` other than the mod definition is written
by `Editor/genassets.py` from a spec list. **Do not hand-edit those files** — change the spec
and re-run it. Guids and the 128-bit addresses Core Keeper links sprites and text with are
derived from each asset's path, so regenerating is a no-op:

```
python3 Editor/genassets.py            # write or refresh
python3 Editor/genassets.py --check    # fail if anything on disk is stale
python3 Editor/preflight.py            # prefab references, banned namespaces, .meta pairs
```

Neither is a compiler. Type errors only surface in the Windows build.

Project documentation in `Editor/Docs/` is written in Korean.

## License

MIT — see [LICENSE.md](LICENSE.md).
