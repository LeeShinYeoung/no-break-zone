# Changelog

## 1.1.0

### New

- All four items now appear in the creative mode item window, each beside its vanilla neighbours:
  the pylon with the electronics, the workbench with the crafting stations, and the lens and the
  remote with the other tools. The game lists no mod's items there on its own.
- Every item has its own 10×10 icon for when it lies on the floor, sits in a hand or shows up in a
  crafting list. Before this, the full-size icon was used and looked oversized.

### Fixed

- Raising the Pylon Lens with several pylons out no longer stalls for a moment.
- The lens outline no longer jumps by a tile while you walk.
- Switching a pylon is cheaper. Each change now re-checks the loaded world once and in bulk; it
  used to do it twice, one object at a time.

### Known gaps

- Never played by two people at once.
- Switching a pylon still causes a short hitch, about 20–30 ms per world in a test world.

## 1.0.0 — first release

The mod is feature complete against its design and has been played through by hand: crafted,
placed, switched, protected, released, collected.

### What it does

- **No Break Pylon.** Place it and press **E**. While it is on, nothing inside the square around it
  can be destroyed — chests, walls, floors, machines, and the pylon itself. Press **E** again and
  the area is ordinary again. The on/off state survives saving and loading.
- **Pylon Workbench**, crafted at a vanilla Automation Table, makes the pylon and its two tools.
- **Pylon Lens.** Hold it and the edge of every switched-on pylon's square is drawn. Nothing else
  ever draws it.
- **Pylon Remote.** Switch a pylon from up to 30 tiles away.
- Four settings: the size of the square, whether mobs and explosions are blocked too, whether the
  lens draws anything, and how far the remote reaches.

### What it deliberately will not do

Anything that pays out every time it is damaged — a drill's ore boulder above all — is never
protected. Protecting one would duplicate resources and break a save permanently, and that rule
outranks every other property of this mod.

### Languages

English and Korean. The remaining eleven languages the game supports fall back to English, and the
structure takes a translation as a single line of text whenever one arrives.

### Known gaps

- Never played by two people at once. It has run in a world that predates it, and on a dedicated
  server with a client connected.
