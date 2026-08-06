#!/usr/bin/env python3
"""Generate the mod's Unity assets from a spec instead of writing YAML by hand.

    python3 Editor/genassets.py            # write/refresh every asset
    python3 Editor/genassets.py --check    # fail if anything on disk is stale

A placeable object in Core Keeper is not one file. It is a logic prefab, a graphics prefab, a
SpriteAsset, a TextDataBlock, a texture plus its import settings, an entry in the mod's
SpriteAssetManifest, and a .meta beside every one of them — roughly 1,500 lines of YAML for a
single item. 기획서 6단계 adds three more objects (lens, remote, workbench) built the exact same
way, so the YAML is written once here as templates and the differences live in SPECS below.

WHY THIS CAN WORK WITHOUT UNITY (research.md 11장): prefabs are plain YAML whose only opaque part
is `m_Script: {fileID, guid}`. The guid names an assembly and is copied from reference prefabs; the
fileID is derived from the class name via MD4 and is therefore computable. Editor/preflight.py
reverses every reference we emit back to a real game class, so a typo in a component name fails on
the Mac rather than in the Windows build.

IDENTITY IS DERIVED FROM THE FILE PATH, NEVER RANDOM. Unity stores references as guids and Core
Keeper stores sprite/text links as 128-bit addresses; if either changed between runs, every
reference to it would break and Unity would reimport the world (CLAUDE.md §1-2). Both are hashed
from the asset's repo-relative path, so regenerating is a no-op and a rename is a deliberate act.

WHAT THIS DOES NOT DO: it does not compile anything, and it does not know whether a field value is
one the game accepts. Field *names* come from ck-db/Pug.ECS.Authoring/*.cs and every constant below
cites the reference file it was read from, but "does the game like this number" is answered only by
the Windows build and by looking at the thing in game (CLAUDE.md §2).
"""

import argparse
import csv
import hashlib
import pathlib
import re
import struct
import sys
import uuid
import zlib

REPO = pathlib.Path(__file__).resolve().parent.parent


# ---------------------------------------------------------------------------------------------
# Constants observed in reference assets. Nothing here is invented; each line says where it came
# from so a future game update can be re-checked against the same file.
# ---------------------------------------------------------------------------------------------

# Script references are NOT hardcoded. They used to be — assembly guids copied out of reference
# prefabs and the SDK examples — and every one of them named an assembly that does not exist in
# this SDK install. Unity could not resolve the type, wrote a dangling m_Script into the bundle,
# and the game loaded nothing while the build still reported success.
#
# Editor/GameData/script_guids.csv is Unity's own answer, dumped by Editor/DumpScriptGuids.cs.
# Regenerate it after a game or SDK update and rerun this script.
SCRIPT_GUIDS = REPO / "Editor" / "GameData" / "script_guids.csv"

_script_table: "dict[str, set[tuple[int, str]]] | None" = None


def _load_script_table() -> "dict[str, set[tuple[int, str]]]":
    global _script_table
    if _script_table is None:
        if not SCRIPT_GUIDS.exists():
            sys.exit(
                f"missing {SCRIPT_GUIDS.relative_to(REPO)} — run Editor/DumpScriptGuids.cs first:\n"
                "  Unity.exe -batchmode -quit -projectPath <proj> "
                "-executeMethod NoBreakZone.EditorTools.DumpScriptGuids.Dump"
            )
        table: "dict[str, set[tuple[int, str]]]" = {}
        with SCRIPT_GUIDS.open(newline="", encoding="utf-8") as handle:
            for row in csv.DictReader(handle):
                ref = (int(row["fileID"]), row["guid"])
                full = row["fullName"]
                # Reachable by full name and by short name; short names can collide, which
                # game_script() reports rather than guessing.
                table.setdefault(full, set()).add(ref)
                table.setdefault(full.rsplit(".", 1)[-1], set()).add(ref)
        _script_table = table
    return _script_table


def game_script(class_name: str) -> "tuple[int, str]":
    """The exact (fileID, guid) Unity writes into m_Script for this class.

    Both halves come from the same row, so the pair can never drift apart the way a hand-kept
    fileID constant and a hand-kept assembly guid did.
    """
    refs = _load_script_table().get(class_name)
    if not refs:
        sys.exit(f"unknown class '{class_name}' — not in {SCRIPT_GUIDS.relative_to(REPO)}")
    if len(refs) > 1:
        listed = ", ".join(f"{fid}/{guid}" for fid, guid in sorted(refs))
        sys.exit(f"ambiguous class '{class_name}' ({listed}) — use the namespaced name")
    return next(iter(refs))


GUID_SPRITE_MATERIAL = "571bf3c761ee86c4f9d8e65be27151be"  # SpriteObject.material, both refs

# The assembly our own MonoBehaviours compile into — NoBreakZone.asmdef's "name". UnityEvent stores
# its target as "<type>, <assembly>", so this has to match or the call fails to resolve at runtime.
# (The SDK's own workbench says "WorkBenchGraphical, ItemExample" while that script lives under
# WorkbenchExample.asmdef — a stale name left behind when the script moved. Do not copy that.)
MOD_ASSEMBLY = "NoBreakZone"

# fileIDs that name a Unity built-in rather than a class, so game_script() does not apply.
FID_MONOSCRIPT_CS = 11500000  # class lives in a .cs, not a .dll
FID_SCRIPTABLE_OBJECT = 11400000  # the single object inside a .asset
FID_TEXTURE2D = 2800000  # Texture2D subasset — what SpriteAsset.texture points at
FID_SPRITE = 21300000  # Sprite subasset of a spriteMode:1 texture — what an item icon points at

OBJECT_TYPE_PLACEABLE_PREFAB = 800  # ObjectType.PlaceablePrefab
OBJECT_TYPE_KEY_ITEM = 1500  # ObjectType.KeyItem — held, no mechanical use of its own
TAG_CAN_BE_SALVAGED = "19000000"  # List<ObjectCategoryTag>{CanBeSalvaged}; Unity's packed int form

# The game's 13 language addresses, copied verbatim from the SDK WorkbenchExample TextDataBlock.
# Which entry is which language is still unknown (research.md 11장); the SDK writes the same English
# text into all 13 and so do we. 기획서 §4 ships English + Korean, split in 7단계.
LANGUAGE_ADDRESSES = [
    (8319415704751845611, -6023042414290333943),
    (-2957573344710624914, 6297677370195620808),
    (681352171529052915, 2314507893210082619),
    (6040640903323332866, 4472318406855671320),
    (5554070574462901973, -3923471906115629047),
    (4644340974835219079, -8672482724880852038),
    (-7722434995445778156, 2655981564484923289),
    (-2052239290651699458, -8389256263833660117),
    (7851011593462244222, 4794823751170882376),
    (-998452581336238672, -1236628366252422438),
    (555117963105384372, 3194402766475108890),
    (7837645069971566515, 4238944138262099067),
    (7031320391619404414, 5017443214120506826),
]
PRIMARY_LANGUAGE_INDEX = 2  # the entry the SDK example mirrors into m_prevImportPrimaryEntry

# Which slot above is which language — UNKNOWN, and it is the only thing standing between this mod
# and the Korean release 기획서 §4 asks for.
#
# The addresses are guids of LanguageDataBlock assets inside the game's own bundles. Reversing all
# thirteen and searching ck-db, ck-mods, CoreLib and the SDK examples turns up nothing, and no
# reference mod localises to anything but English, so there is nothing to copy.
#
# HOW TO FILL THIS IN (Windows, one lookup): open a TextDataBlock of ours in Unity's Scriptable Data
# Editor. It shows each slot's language by name. Note the position of Korean — 0-based, matching the
# order of LANGUAGE_ADDRESSES — and put it here. Every Korean string in SPECS then lands in the
# right place on the next run.
LANGUAGE_SLOTS = {
    # "ko": <index>,
}


# ---------------------------------------------------------------------------------------------
# Derived identity. Every id is a hash of the asset path, so running this twice changes nothing.
# ---------------------------------------------------------------------------------------------

def _digest(kind: str, key: str) -> bytes:
    return hashlib.md5(f"NoBreakZone/{kind}/{key}".encode("utf-8")).digest()


def asset_guid(rel_path: str) -> str:
    """Unity guid for an asset, from its repo-relative path. 32 hex chars, same shape Unity emits."""
    return _digest("guid", rel_path).hex()


def local_file_id(rel_path: str, node: str) -> int:
    """Id of one object inside a prefab. Signed 64-bit, like the ones Unity generates."""
    value = struct.unpack("<q", _digest("fileid", f"{rel_path}#{node}")[:8])[0]
    return value if value != 0 else 1  # 0 means "null reference" in Unity YAML


def data_block_address(rel_path: str) -> tuple:
    """(m_low, m_high) for a DataBlockAddress — how sprites and text are linked (research.md 11장).

    It is the asset's own Unity guid: a 128-bit GUID laid out the Microsoft way (first three fields
    little-endian), split into two signed 64-bit halves. DataBlockAddress is built from GUID strings
    in the game's own code (ck-db Pug.Base/ContentBundleDataBlock.cs), and reversing the SDK
    examples' values through this layout reproduces each file's .meta guid — 18 of 18.

    Strictly the game only needs the value to be unique: the reference mods' assets do NOT match
    their file guids and still work. Following the SDK's convention costs nothing and means our
    assets are indistinguishable from generated ones.
    """
    return struct.unpack("<qq", uuid.UUID(hex=asset_guid(rel_path)).bytes_le)


# ---------------------------------------------------------------------------------------------
# Guarding 기획서 §7's promise about the lit state.
#
# §7 wants the two states to share a silhouette: "이렇게 하면 두 상태의 실루엣이 완전히 동일해
# 전환 시 튀지 않는다." The drafts satisfy it — pylon_off and pylon_on come out of one shape
# function and differ only in colour — and the check below refuses to generate anything that stops
# satisfying it.
# ---------------------------------------------------------------------------------------------

def _read_png_rgba(path: pathlib.Path):
    """Decode an 8-bit RGBA PNG into (width, height, rows of bytes). Enough for our own art only."""
    data = path.read_bytes()
    pos, idat = 8, b""
    width = height = None
    while pos < len(data):
        length = struct.unpack(">I", data[pos:pos + 4])[0]
        kind = data[pos + 4:pos + 8]
        chunk = data[pos + 8:pos + 8 + length]
        if kind == b"IHDR":
            width, height, depth, colour = struct.unpack(">IIBB", chunk[:10])
            if (depth, colour) != (8, 6):
                raise ValueError(f"{path}: expected 8-bit RGBA, got depth {depth} colour type {colour}")
        elif kind == b"IDAT":
            idat += chunk
        pos += 12 + length

    raw = zlib.decompress(idat)
    stride = width * 4
    rows, previous, at = [], bytearray(stride), 0
    for _ in range(height):
        filter_type = raw[at]
        at += 1
        line = bytearray(raw[at:at + stride])
        at += stride
        for x in range(stride):
            left = line[x - 4] if x >= 4 else 0
            up = previous[x]
            up_left = previous[x - 4] if x >= 4 else 0
            if filter_type == 1:
                line[x] = (line[x] + left) & 0xFF
            elif filter_type == 2:
                line[x] = (line[x] + up) & 0xFF
            elif filter_type == 3:
                line[x] = (line[x] + (left + up) // 2) & 0xFF
            elif filter_type == 4:
                estimate = left + up - up_left
                da, db, dc = abs(estimate - left), abs(estimate - up), abs(estimate - up_left)
                nearest = left if (da <= db and da <= dc) else (up if db <= dc else up_left)
                line[x] = (line[x] + nearest) & 0xFF
            elif filter_type != 0:
                raise ValueError(f"{path}: unknown PNG filter {filter_type}")
        rows.append(bytes(line))
        previous = line
    return width, height, rows


def _write_png_rgba(width: int, height: int, rows) -> bytes:
    """Encode 8-bit RGBA with no row filtering. Deterministic, so --check stays meaningful."""
    def chunk(kind: bytes, payload: bytes) -> bytes:
        return (struct.pack(">I", len(payload)) + kind + payload
                + struct.pack(">I", zlib.crc32(kind + payload) & 0xFFFFFFFF))

    raw = b"".join(b"\x00" + row for row in rows)
    return (
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0))
        + chunk(b"IDAT", zlib.compress(raw, 9))
        + chunk(b"IEND", b"")
    )


def check_same_silhouette(base_art: pathlib.Path, lit_art: pathlib.Path) -> None:
    """Fail unless a variation's art has the same outline as variation 0's, and differs somewhere.

    기획서 §7 asks for one sprite whose lit state cannot shift the shape: "이렇게 하면 두 상태의
    실루엣이 완전히 동일해 전환 시 튀지 않는다." Since a variation now carries its own texture
    rather than an emissive overlay, nothing structural enforces that any more — so it is checked.
    Alpha equal everywhere is exactly "same silhouette", and it is free to verify.

    Both textures come out of the same shape function in Editor/Docs/art/sprites.py, which only ever
    recolours opaque pixels for the lit state, so this passes by construction. It is here to catch
    the day somebody draws one of them by hand.
    """
    width, height, base = _read_png_rgba(base_art)
    lit_width, lit_height, lit = _read_png_rgba(lit_art)
    if (width, height) != (lit_width, lit_height):
        raise ValueError(f"{base_art.name} is {width}x{height} but {lit_art.name} is "
                         f"{lit_width}x{lit_height}; they must line up pixel for pixel")

    changed = 0
    for y in range(height):
        for x in range(width):
            span = slice(x * 4, x * 4 + 4)
            if base[y][span] != lit[y][span]:
                changed += 1
            if base[y][x * 4 + 3] != lit[y][x * 4 + 3]:
                raise ValueError(
                    f"{lit_art.name} differs from {base_art.name} in alpha at ({x},{y}) — the two "
                    "states would have different silhouettes and the object would jump when it "
                    "switched (기획서 §7)")

    if changed == 0:
        raise ValueError(f"{lit_art.name} is identical to {base_art.name} — switching the object on "
                         "would look like nothing happened")


# ---------------------------------------------------------------------------------------------
# The lens's range marker (기획서 §7).
#
# ONE STRAIGHT SEGMENT, not a tile stamp. 기획서 §9 forbids drawing the range by making an object
# per tile — "21×21이면 경계만 해도 80칸이다" — so the overlay stretches four of these into the four
# sides of the square instead.
#
# Drawn here rather than by hand because it is a straight line, and because these numbers are the
# ones worth turning after seeing it in game: 기획서 §7 warns that a marker which spoils a decorated
# base gets the whole mod uninstalled, so "아주 옅은 윤곽" is the target.
# ---------------------------------------------------------------------------------------------

# Must stay in step with NoBreakZoneRangeOverlay.MarkerSpriteName, which finds the sprite by name.
RANGE_MARKER_TEXTURE = "Textures/NoBreakZoneRangeMarker.png"

MARKER_TILE_PIXELS = 16  # one tile; SpriteObject.PixelsPerUnit is a hardcoded 16f
MARKER_THICKNESS_PIXELS = 2
MARKER_COLOUR = (150, 220, 255)  # pale cyan, to read as "information" rather than as decoration
MARKER_ALPHA = 90  # out of 255


def range_marker_png() -> bytes:
    """A solid horizontal segment one tile long.

    Uniform along its length, which is what lets the overlay scale it to any edge without the
    pattern distorting — only the length axis is scaled, so the line keeps its thickness.
    """
    red, green, blue = MARKER_COLOUR
    row = bytes((red, green, blue, MARKER_ALPHA)) * MARKER_TILE_PIXELS
    return _write_png_rgba(MARKER_TILE_PIXELS, MARKER_THICKNESS_PIXELS,
                           [row] * MARKER_THICKNESS_PIXELS)


# ---------------------------------------------------------------------------------------------
# The spec. 6단계 adds lens/remote/workbench by appending here, not by writing YAML.
# ---------------------------------------------------------------------------------------------

class ObjectSpec:
    """One object the mod adds, in either of the two shapes the game recognises.

    A PLACEABLE (objectType 800) is a thing in the world: logic prefab, graphics prefab, SpriteAsset,
    manifest entry, physics, netcode ghost, state machine.

    An ITEM (anything else — the lens is KeyItem) only ever sits in an inventory or a hand, and
    needs none of that. The SDK's own Sword1 carries three components — ObjectAuthoring,
    InventoryItemAuthoring, LocalizationAuthoring — with graphicalPrefab left at 0 and no SpriteAsset
    anywhere, its icon pointing straight at a sprite in the PNG.
    """

    def __init__(self, key, object_name, title, description, art,
                 object_type=OBJECT_TYPE_PLACEABLE_PREFAB,
                 tile_size=(1, 1), pixels_to_units=16, stackable=True, rarity=3,
                 health=10, recipe=(), crafting_time=3.0,
                 sprite_offset=(0, 0.5625, -0.3125),
                 crafts=(), graphics_script=None, ui_titles=(),
                 variants=(), interact_method=None,
                 variation_is_dynamic=False, variation_to_toggle_to=0,
                 localized=None):
        self.key = key  # asset base name, for everything the game finds by guid or by address
        # ObjectID string — 기획서 §4, never change (CLAUDE.md §5). Also the localization term: the
        # game looks item text up by this, so the TextDataBlock and termKey are named from it too.
        self.object_name = object_name
        self.title = title  # English, and the fallback for every slot without a translation
        self.description = description
        # {language code: (title, description)}. 기획서 §4 ships English and Korean, and wants the
        # structure to take all thirteen from the start. Text written here only reaches the asset
        # once LANGUAGE_SLOTS knows which slot that language is — writing it now means the Windows
        # session that discovers the mapping does not also have to translate.
        self.localized = dict(localized or {})
        self.art = art  # source PNG under Editor/Docs/art
        self.object_type = object_type
        self.tile_size = tile_size
        self.pixels_to_units = pixels_to_units
        self.stackable = stackable
        self.rarity = rarity
        self.health = health
        self.recipe = list(recipe)  # [(objectName, amount)] — where it is craftable is 6단계
        self.crafting_time = crafting_time
        # Where the sprite quad sits relative to the object. The quad is centred on its pivot
        # (0.5, 0.5) and is texture_height/16 units tall, so for the 16x18 art it reaches 0.5625
        # units either side of this point — meaning y has to be 0.5625 for the bottom row to land on
        # the ground.
        #
        # The SDK workbench's 0.0625 was copied verbatim and put the bottom half a unit UNDER the
        # floor: in game the pylon and the workbench were both sliced off across the middle, showing
        # roughly their top ten rows of eighteen, which is exactly 0.5 units of sinking. That example
        # has never been built by anyone (research.md 11장 records the same lesson about its guids),
        # so its numbers are not evidence.
        self.sprite_offset = sprite_offset

        # A crafting station. Non-empty means the logic prefab gets CraftingAuthoring and the
        # graphics prefab grows an InteractableObject so E opens the crafting UI.
        self.crafts = list(crafts)  # [objectName] — modded ids, resolved by name at bake time
        # Repo-relative .cs whose class becomes the graphics prefab's root component. Needed for a
        # station because the root has to derive from CraftingBuilding; None uses stock
        # EntityMonoBehaviour, which is all a plain placeable needs.
        self.graphics_script = graphics_script
        # I2 localization terms for the crafting window header, as the SDK example uses them.
        self.ui_titles = list(ui_titles)

        # Extra looks the object can switch between, as [(suffix, art)], becoming variation 1, 2, …
        # in SpriteAsset.m_staticVariants. Each is a whole texture of its own, the way every variant
        # in the SDK example is; check_same_silhouette keeps them the same shape as variation 0.
        self.variants = list(variants)
        # Method on graphics_script's class that InteractableObject calls on E. None means the
        # object cannot be interacted with at all.
        self.interact_method = interact_method
        # 기획서 §5's on/off switch, handled entirely by the game once these are set.
        self.variation_is_dynamic = variation_is_dynamic
        self.variation_to_toggle_to = variation_to_toggle_to

    @property
    def is_placeable(self):
        """Drives which half of the component set the logic prefab gets, and whether there is any
        graphics prefab or SpriteAsset to generate at all."""
        return self.object_type == OBJECT_TYPE_PLACEABLE_PREFAB

    @property
    def graphics_class(self):
        return pathlib.PurePosixPath(self.graphics_script).stem if self.graphics_script else None

    def variant_texture_path(self, suffix):
        return f"Textures/{self.key}{suffix}.png"

    # --- paths -------------------------------------------------------------------------------
    @property
    def texture_path(self):
        return f"Textures/{self.key}.png"

    @property
    def sprite_asset_path(self):
        return f"Data/SpriteAsset/{self.key}.asset"

    @property
    def text_path(self):
        # Both reference mods put item text under TextDataBlock/Items/, so we match them.
        #
        # NAMED AFTER object_name, NOT key. The game looks an item's text up by the object's own
        # name: with a TextDataBlock called NoBreakZoneWorkbench the game asked for
        # "Items/NoBreakZone.Workbench" and drew "missing: Items/NoBreakZone.Workbench" in the
        # tooltip. The SDK example never showed this up because its objectName, its termKey, its
        # TextDataBlock's m_Name and that asset's filename are all the one string
        # ("MyNewWorkbench1"), and nothing anywhere references the block by guid -- the name is the
        # only link there is. So all four have to agree, and object_name is the one we cannot move
        # (CLAUDE.md §5: it is written into saves).
        return f"Data/TextDataBlock/Items/{self.object_name}.asset"

    @property
    def logic_path(self):
        return f"Prefabs/{self.key}.prefab"

    @property
    def graphics_path(self):
        return f"Prefabs/{self.key}Graphics.prefab"


SPECS = [
    ObjectSpec(
        key="NoBreakZonePylon",
        object_name="NoBreakZone.Pylon",  # 기획서 §4. Written into saves — changing it breaks them.
        title="No Break Pylon",
        description="Protects nearby objects. While it is on, nothing inside can be destroyed.",
        localized={"ko": ("파일런",
                          "주변의 물건을 보호한다. 켜져 있는 동안에는 어떤 충격도 그 안의 것들을 부수지 못한다.")},
        art="Editor/Docs/art/pylon_off.png",
        # 기획서 §4: 1x1 tiles, and the 16x18 art now draws at exactly that (a tile is 16px, which
        # is hardcoded in SpriteObject.PixelsPerUnit). The 32px draft covered 2x2.
        tile_size=(1, 1),
        pixels_to_units=16,
        stackable=True,
        rarity=3,
        health=10,
        # 기획서 §4: iron + ancient gemstone + mechanical part. Nothing lists the pylon as craftable
        # until the workbench exists (6단계), so this recipe is inert for now.
        recipe=[("IronBar", 8), ("AncientGemstone", 1), ("MechanicalPart", 2)],
        crafting_time=3.0,
        # 기획서 §5: E toggles it, a freshly placed one starts off (variation stays 0 above), and
        # the state survives save/load because ObjectDataCD is part of the world save. The game does
        # all of it — see Scripts/Graphics/NoBreakZonePylonGraphics.cs.
        variation_is_dynamic=True,
        variation_to_toggle_to=1,
        # Variation 1 is the lit pylon: the gem burns and the light spreads into the body, which is
        # what makes the switch readable across a base. Same silhouette as variation 0 — same shape
        # function, only recoloured — and check_same_silhouette proves it every run (기획서 §7).
        variants=[("On", "Editor/Docs/art/pylon_on.png")],
        graphics_script="Scripts/Graphics/NoBreakZonePylonGraphics.cs",
        interact_method="Toggle",
    ),
    ObjectSpec(
        key="NoBreakZoneWorkbench",
        object_name="NoBreakZone.Workbench",  # 기획서 §4. Written into saves — do not change.
        title="Pylon Workbench",
        description="Where the pylon and its tools are made.",
        localized={"ko": ("파일런 작업대", "파일런과 그에 딸린 도구를 만드는 곳.")},
        art="Editor/Docs/art/workbench.png",
        # ONE TILE, not 기획서 §4's original 2x1 — changed with the user's approval after seeing it
        # placed, and design.md §4 carries the decision record. The 64x32 draft drew four tiles wide
        # and two tall over a two-tile footprint; a bench that reaches past its own footprint is
        # worse in a cramped base than a smaller one, and 1x1 is what the SDK's own workbench is.
        tile_size=(1, 1),
        pixels_to_units=16,
        stackable=True,
        rarity=3,
        health=10,
        # 기획서 §4: iron + wood + one mechanical part. Made at a vanilla iron-tier bench, which
        # NoBreakZoneWorkbenchRecipeInjectionConverter arranges — nothing in this file can, because
        # the bench belongs to the game rather than to us.
        recipe=[("IronBar", 12), ("Wood", 20), ("MechanicalPart", 1)],
        crafting_time=3.0,
        # 기획서 §4 lists exactly these three, and this completes them.
        crafts=["NoBreakZone.Pylon", "NoBreakZone.Lens", "NoBreakZone.Remote"],
        graphics_script="Scripts/Graphics/NoBreakZoneWorkbenchGraphics.cs",
        interact_method="Use",  # CraftingBuilding.Use — opens the crafting window
        ui_titles=["gear", "crafting", "base"],  # same three terms the SDK workbench uses
    ),
    ObjectSpec(
        key="NoBreakZoneLens",
        object_name="NoBreakZone.Lens",  # 기획서 §4. Written into saves — do not change.
        title="Pylon Lens",
        description="Hold it to see the edge of every active pylon's protection.",
        localized={"ko": ("파일런 렌즈",
                          "파일런의 파편을 깎아 만든 렌즈. 들고 있으면 보호의 경계가 드러난다.")},
        art="Editor/Docs/art/lens.png",
        # 기획서 §4 calls it 도구, "손에 드는 물건, 착용 장비가 아님", and it does nothing when
        # used — its whole effect is the overlay that runs while it is held. KeyItem is the game's
        # type for exactly that: carried, no mechanical use of its own.
        object_type=OBJECT_TYPE_KEY_ITEM,
        # 16, matching the 16x16 art. This one really is only the inventory icon — a KeyItem never
        # stands in the world — but the whole set is drawn to one scale so the icons match.
        pixels_to_units=16,
        # 기획서 §4: "렌즈와 리모콘은 스택되지 않는다. 여러 개를 가질 이유가 없는 물건이고,
        # 겹쳐지면 인벤토리에서 개수만 헷갈린다."
        stackable=False,
        rarity=3,
        recipe=[("IronBar", 6), ("AncientGemstone", 1)],  # 기획서 §4: 철 + 고대 보석 1
        crafting_time=3.0,
    ),
    ObjectSpec(
        key="NoBreakZoneRemote",
        object_name="NoBreakZone.Remote",  # 기획서 §4. Written into saves — do not change.
        title="Pylon Remote",
        description="Right-click a pylon from a distance to switch it on or off.",
        localized={"ko": ("파일런 리모콘", "멀리서 파일런을 켜고 끈다. 커서를 올리고 우클릭.")},
        art="Editor/Docs/art/remote.png",
        # Same shape as the lens: carried, and what it does happens in a system reading the player's
        # input rather than through any slot behaviour the game would attach to a usable type.
        object_type=OBJECT_TYPE_KEY_ITEM,
        pixels_to_units=16,  # same as the lens
        stackable=False,  # 기획서 §4, same reasoning as the lens
        rarity=3,
        recipe=[("IronBar", 6), ("MechanicalPart", 2)],  # 기획서 §4: 철 + 기계부품
        crafting_time=3.0,
    ),
]


# ---------------------------------------------------------------------------------------------
# YAML emitters
# ---------------------------------------------------------------------------------------------

YAML_HEADER = "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n"


def folder_meta(rel_path: str) -> str:
    return (
        "fileFormatVersion: 2\n"
        f"guid: {asset_guid(rel_path)}\n"
        "folderAsset: yes\n"
        "DefaultImporter:\n"
        "  externalObjects: {}\n"
        "  userData: \n"
        "  assetBundleName: \n"
        "  assetBundleVariant: \n"
    )


def asset_meta(rel_path: str) -> str:
    """.meta for a .asset (ScriptableObject). NativeFormatImporter, per both reference mods."""
    return (
        "fileFormatVersion: 2\n"
        f"guid: {asset_guid(rel_path)}\n"
        "NativeFormatImporter:\n"
        "  externalObjects: {}\n"
        "  mainObjectFileID: 11400000\n"
        "  userData: \n"
        "  assetBundleName: \n"
        "  assetBundleVariant: \n"
    )


def prefab_meta(rel_path: str) -> str:
    return (
        "fileFormatVersion: 2\n"
        f"guid: {asset_guid(rel_path)}\n"
        "PrefabImporter:\n"
        "  externalObjects: {}\n"
        "  userData: \n"
        "  assetBundleName: \n"
        "  assetBundleVariant: \n"
    )


def texture_meta(rel_path: str, pixels_to_units: int) -> str:
    """Sprite import settings, from ck-mods ConveyorTunnelIcon.png.meta (spriteMode 1 = one sprite).

    filterMode 0 (point) and textureType 8 (sprite) are what every reference uses; anything else
    blurs pixel art. spritePixelsToUnits sizes the Sprite subasset, which is what an inventory icon
    is drawn from. It does NOT size the object in the world: SpriteAsset points at the Texture2D and
    PugSprite scales by its own SpriteObject.PixelsPerUnit = 16f (ck-db PugSprite/SpriteObject.cs:1096).
    """
    return (
        "fileFormatVersion: 2\n"
        f"guid: {asset_guid(rel_path)}\n"
        "TextureImporter:\n"
        "  internalIDToNameTable: []\n"
        "  externalObjects: {}\n"
        "  serializedVersion: 13\n"
        "  mipmaps:\n"
        "    mipMapMode: 0\n"
        "    enableMipMap: 0\n"
        "    sRGBTexture: 1\n"
        "    linearTexture: 0\n"
        "    fadeOut: 0\n"
        "    borderMipMap: 0\n"
        "    mipMapsPreserveCoverage: 0\n"
        "    alphaTestReferenceValue: 0.5\n"
        "    mipMapFadeDistanceStart: 1\n"
        "    mipMapFadeDistanceEnd: 3\n"
        "  bumpmap:\n"
        "    convertToNormalMap: 0\n"
        "    externalNormalMap: 0\n"
        "    heightScale: 0.25\n"
        "    normalMapFilter: 0\n"
        "    flipGreenChannel: 0\n"
        "  isReadable: 0\n"
        "  streamingMipmaps: 0\n"
        "  streamingMipmapsPriority: 0\n"
        "  vTOnly: 0\n"
        "  ignoreMipmapLimit: 0\n"
        "  grayScaleToAlpha: 0\n"
        "  generateCubemap: 6\n"
        "  cubemapConvolution: 0\n"
        "  seamlessCubemap: 0\n"
        "  textureFormat: 1\n"
        "  maxTextureSize: 2048\n"
        "  textureSettings:\n"
        "    serializedVersion: 2\n"
        "    filterMode: 0\n"
        "    aniso: 1\n"
        "    mipBias: 0\n"
        "    wrapU: 1\n"
        "    wrapV: 1\n"
        "    wrapW: 1\n"
        "  nPOTScale: 0\n"
        "  lightmap: 0\n"
        "  compressionQuality: 50\n"
        "  spriteMode: 1\n"
        "  spriteExtrude: 1\n"
        "  spriteMeshType: 0\n"
        "  alignment: 0\n"
        "  spritePivot: {x: 0.5, y: 0.5}\n"
        f"  spritePixelsToUnits: {pixels_to_units}\n"
        "  spriteBorder: {x: 0, y: 0, z: 0, w: 0}\n"
        "  spriteGenerateFallbackPhysicsShape: 1\n"
        "  alphaUsage: 1\n"
        "  alphaIsTransparency: 1\n"
        "  spriteTessellationDetail: -1\n"
        "  textureType: 8\n"
        "  textureShape: 1\n"
        "  singleChannelComponent: 0\n"
        "  flipbookRows: 1\n"
        "  flipbookColumns: 1\n"
        "  maxTextureSizeSet: 0\n"
        "  compressionQualitySet: 0\n"
        "  textureFormatSet: 0\n"
        "  ignorePngGamma: 0\n"
        "  applyGammaDecoding: 0\n"
        "  swizzle: 50462976\n"
        "  cookieLightType: 0\n"
        "  platformSettings:\n"
        + "".join(
            "  - serializedVersion: 4\n"
            f"    buildTarget: {target}\n"
            "    maxTextureSize: 2048\n"
            "    resizeAlgorithm: 0\n"
            "    textureFormat: -1\n"
            "    textureCompression: 0\n"
            "    compressionQuality: 50\n"
            "    crunchedCompression: 0\n"
            "    allowsAlphaSplitting: 0\n"
            "    overridden: 0\n"
            "    ignorePlatformSupport: 0\n"
            "    androidETC2FallbackOverride: 0\n"
            "    forceMaximumCompressionQuality_BC6H_BC7: 0\n"
            for target in ("DefaultTexturePlatform", "Standalone", "Server")
        )
        + "  spriteSheet:\n"
        "    serializedVersion: 2\n"
        "    sprites: []\n"
        "    outline: []\n"
        "    customData: \n"
        "    physicsShape: []\n"
        "    bones: []\n"
        # Fixed in all 14 reference textures — not a per-asset id (research.md 11장).
        "    spriteID: 5e97eb03825dee720800000000000000\n"
        "    internalID: 0\n"
        "    vertices: []\n"
        "    indices: \n"
        "    edges: []\n"
        "    weights: []\n"
        "    secondaryTextures: []\n"
        "    spriteCustomMetadata:\n"
        "      entries: []\n"
        "    nameFileIdTable: {}\n"
        "  mipmapLimitGroupName: \n"
        "  pSDRemoveMatte: 0\n"
        "  userData: \n"
        "  assetBundleName: \n"
        "  assetBundleVariant: \n"
    )


def _scriptable_header(name: str, fileid: int, guid: str) -> str:
    return (
        YAML_HEADER
        + f"--- !u!114 &{FID_SCRIPTABLE_OBJECT}\n"
        "MonoBehaviour:\n"
        "  m_ObjectHideFlags: 0\n"
        "  m_CorrespondingSourceObject: {fileID: 0}\n"
        "  m_PrefabInstance: {fileID: 0}\n"
        "  m_PrefabAsset: {fileID: 0}\n"
        "  m_GameObject: {fileID: 0}\n"
        "  m_Enabled: 1\n"
        "  m_EditorHideFlags: 0\n"
        f"  m_Script: {{fileID: {fileid}, guid: {guid}, type: 3}}\n"
        f"  m_Name: {name}\n"
        "  m_EditorClassIdentifier: \n"
    )


def _address(indent: str, low: int, high: int) -> str:
    return f"{indent}m_address:\n{indent}  m_low: {low}\n{indent}  m_high: {high}\n"


def _null_address(indent: str) -> str:
    return _address(indent, 0, 0)


def sprite_asset(spec: ObjectSpec) -> str:
    """SpriteAsset: the thing a graphics prefab's SpriteObject resolves through its m_address.

    m_staticSpriteData is variation 0; m_staticVariants holds variation 1 upwards, which is how the
    pylon's on state is drawn (기획서 §5). 기획서 §7's glow will go in emissiveTexture rather than a
    separate sprite (research.md 11장).
    """
    low, high = data_block_address(spec.sprite_asset_path)
    texture_guid = asset_guid(spec.texture_path)
    variants = "".join(
        # A VARIATION SWAPS THE WHOLE TEXTURE. It used to keep variation 0's texture and add an
        # emissiveTexture holding just the lit pixels, on the reading that 기획서 §7's "스프라이트는
        # 한 종류만 만들고 발광 레이어를 켜고 끄는 방식" meant the emissive slot. In game the pylon
        # looked exactly the same switched on and off. Every variant in the SDK example swaps
        # `texture` and leaves emissiveTexture at 0, and no reference anywhere drives a variation
        # through the emissive slot — so this follows the shape that is known to work.
        #
        # §7's actual promise is that the two states share a silhouette, and that still holds by
        # construction: both textures come out of the same shape function in sprites.py, so their
        # alpha is identical to the byte (asserted below in build_outputs).
        f"  - texture: {{fileID: {FID_TEXTURE2D}, "
        f"guid: {asset_guid(spec.variant_texture_path(suffix))}, type: 3}}\n"
        "    emissiveTexture: {fileID: 0}\n"
        "    normalTexture: {fileID: 0}\n"
        "    pivot: {x: 0.5, y: 0.5}\n"
        "    positionalData: []\n"
        # Unlike the SDK example's variants, we inherit the base pivot: every variation of ours is
        # the same art at the same size, so a variant that centred itself differently would make the
        # pylon jump when switched on.
        "    inheritPivot: 1\n"
        for suffix, _ in spec.variants
    )
    return (
        _scriptable_header(spec.key, *game_script("Pug.Sprite.SpriteAsset"))
        + "  m_overload:\n" + _null_address("    ")
        + _address("  ", low, high)
        + "  m_dynamicCollections:\n"
        "    m_list: []\n"
        # Colour comes straight from the PNG. Grayscale + GradientMap is the skin system, which is
        # optional — the reference mod paints its art directly and so do we (status.md).
        + "  m_defaultPrimaryGradientMap:\n" + _null_address("    ")
        + "  m_defaultSecondaryGradientMap:\n" + _null_address("    ")
        + "  m_defaultTertiaryGradientMap:\n" + _null_address("    ")
        + "  m_defaultPrimaryGradientMapRef:\n" + _null_address("    ")
        + "  m_defaultSecondaryGradientMapRef:\n" + _null_address("    ")
        + "  m_defaultTertiaryGradientMapRef:\n" + _null_address("    ")
        + "  m_editorHideEmissive: 0\n"
        "  m_staticSpriteData:\n"
        f"    texture: {{fileID: {FID_TEXTURE2D}, guid: {texture_guid}, type: 3}}\n"
        "    emissiveTexture: {fileID: 0}\n"
        "    normalTexture: {fileID: 0}\n"
        "    pivot: {x: 0.5, y: 0.5}\n"
        "    positionalData: []\n"
        "    inheritPivot: 1\n"
        + ("  m_staticVariants: []\n" if not variants else "  m_staticVariants:\n" + variants)
        + "  m_animations: []\n"
        "  m_events: []\n"
        "  m_positionalData: []\n"
        "  references:\n"
        "    version: 2\n"
        "    RefIds: []\n"
    )


def text_data_block(spec: ObjectSpec) -> str:
    """Item name and description, in every language the game has a slot for.

    This IS the localisation (official docs, how-to-localize-your-mod.md): the runtime looks names
    up in a TextDataBlock keyed by its own name, under a Header of "Items". A Localization.csv is
    only an import convenience inside the Unity editor and is never read at runtime — the docs tell
    you to move it away as a backup once imported.

    Every slot gets English unless a translation exists for that slot's language. Filling all
    thirteen is what the SDK example does, and it is why an item has a name in every language rather
    than a blank where its title should be.
    """
    low, high = data_block_address(spec.text_path)
    keys = "".join(f"    - m_low: {lo}\n      m_high: {hi}\n" for lo, hi in LANGUAGE_ADDRESSES)

    # index -> (title, description), for the languages whose slot we know
    by_slot = {}
    for code, (title, description) in spec.localized.items():
        slot = LANGUAGE_SLOTS.get(code)
        if slot is not None and 0 <= slot < len(LANGUAGE_ADDRESSES):
            by_slot[slot] = (title, description)

    values = ""
    for index, (lo, hi) in enumerate(LANGUAGE_ADDRESSES):
        title, description = by_slot.get(index, (spec.title, spec.description))
        values += (
            "    - m_language:\n"
            + _address("        ", lo, hi)
            + f"      title: {title}\n"
            f"      description: {description}\n"
        )
    primary_low, primary_high = LANGUAGE_ADDRESSES[PRIMARY_LANGUAGE_INDEX]
    return (
        # m_Name is what the runtime sees (a mod is handed loaded objects, never paths), and it is
        # the half of the lookup that has to match the object's name. See ObjectSpec.text_path.
        _scriptable_header(spec.object_name, *game_script("TextDataBlock"))
        + "  m_overload:\n" + _null_address("    ")
        + _address("  ", low, high)
        + "  m_dynamicCollections:\n"
        "    m_list: []\n"
        "  m_localizedTexts:\n"
        "    keys:\n" + keys
        + "    values:\n" + values
        + "  m_localizationHint: \n"
        "  m_prevImportPrimaryEntry:\n"
        "    m_language:\n"
        + _address("      ", primary_low, primary_high)
        + f"    title: {spec.title}\n"
        f"    description: {spec.description}\n"
        "  m_shouldBeLocalized: 1\n"
        "  m_header: Items\n"
        "  references:\n"
        "    version: 2\n"
        "    RefIds: []\n"
    )


def sprite_asset_manifest(specs) -> str:
    """The mod's index of its SpriteAssets. Both reference mods keep it at the mod root."""
    entries = "".join(
        f"  - {{fileID: {FID_SCRIPTABLE_OBJECT}, guid: {asset_guid(s.sprite_asset_path)}, type: 2}}\n"
        for s in specs
    )
    return (
        _scriptable_header("SpriteAssetManifest", *game_script("Pug.Sprite.SpriteAssetManifest"))
        + "  spriteAssets:\n" + entries
        + "  gradientMaps: []\n"
        "  transformAnimations: []\n"
    )


# --- prefab building blocks ------------------------------------------------------------------

def _game_object(fid: int, name: str, components, layer=0, tag="Untagged") -> str:
    listed = "".join(f"  - component: {{fileID: {c}}}\n" for c in components)
    return (
        f"--- !u!1 &{fid}\n"
        "GameObject:\n"
        "  m_ObjectHideFlags: 0\n"
        "  m_CorrespondingSourceObject: {fileID: 0}\n"
        "  m_PrefabInstance: {fileID: 0}\n"
        "  m_PrefabAsset: {fileID: 0}\n"
        "  serializedVersion: 6\n"
        "  m_Component:\n" + listed
        + f"  m_Layer: {layer}\n"
        f"  m_Name: {name}\n"
        f"  m_TagString: {tag}\n"
        "  m_Icon: {fileID: 0}\n"
        "  m_NavMeshLayer: 0\n"
        "  m_StaticEditorFlags: 0\n"
        "  m_IsActive: 1\n"
    )


def _transform(fid: int, owner: int, parent: int, children=(), position=(0, 0, 0)) -> str:
    listed = (
        "  m_Children: []\n"
        if not children
        else "  m_Children:\n" + "".join(f"  - {{fileID: {c}}}\n" for c in children)
    )
    x, y, z = position
    return (
        f"--- !u!4 &{fid}\n"
        "Transform:\n"
        "  m_ObjectHideFlags: 0\n"
        "  m_CorrespondingSourceObject: {fileID: 0}\n"
        "  m_PrefabInstance: {fileID: 0}\n"
        "  m_PrefabAsset: {fileID: 0}\n"
        f"  m_GameObject: {{fileID: {owner}}}\n"
        "  serializedVersion: 2\n"
        "  m_LocalRotation: {x: 0, y: 0, z: 0, w: 1}\n"
        f"  m_LocalPosition: {{x: {x}, y: {y}, z: {z}}}\n"
        "  m_LocalScale: {x: 1, y: 1, z: 1}\n"
        "  m_ConstrainProportionsScale: 0\n"
        + listed
        + f"  m_Father: {{fileID: {parent}}}\n"
        "  m_LocalEulerAnglesHint: {x: 0, y: 0, z: 0}\n"
    )


def _behaviour(fid: int, owner: int, script_fid: int, guid: str, body: str = "") -> str:
    return (
        f"--- !u!114 &{fid}\n"
        "MonoBehaviour:\n"
        "  m_ObjectHideFlags: 0\n"
        "  m_CorrespondingSourceObject: {fileID: 0}\n"
        "  m_PrefabInstance: {fileID: 0}\n"
        "  m_PrefabAsset: {fileID: 0}\n"
        f"  m_GameObject: {{fileID: {owner}}}\n"
        "  m_Enabled: 1\n"
        "  m_EditorHideFlags: 0\n"
        f"  m_Script: {{fileID: {script_fid}, guid: {guid}, type: 3}}\n"
        "  m_Name: \n"
        "  m_EditorClassIdentifier: \n"
        + body
    )


def _authoring(fid: int, owner: int, class_name: str, body: str = "") -> str:
    """A game authoring component, addressed by class name rather than a copied number.

    A misspelled class name is caught here rather than in the game: game_script() only answers for
    classes Unity actually found, so a typo stops the generator instead of shipping a dead
    reference that builds green.
    """
    return _behaviour(fid, owner, *game_script(class_name), body)


PHYSICS_SHAPE_BODY = (
    # One-tile box collider, copied wholesale from the SDK WorkbenchExample logic prefab. The
    # category masks decide what the object blocks; nothing here is worth deriving from scratch.
    "  m_ShapeType: 0\n"
    "  m_PrimitiveCenter:\n    x: 0\n    y: 0.5\n    z: 0\n"
    "  m_PrimitiveSize:\n    x: 1\n    y: 1\n    z: 1\n"
    "  m_PrimitiveOrientation:\n    Value:\n      x: 0\n      y: 0\n      z: 0\n    RotationOrder: 4\n"
    "  m_Capsule:\n    Height: 1\n    Radius: 0.5\n    Axis: 0\n"
    "  m_Cylinder:\n    Height: 1\n    Radius: 0.5\n    Axis: 2\n"
    "  m_CylinderSideCount: 20\n"
    "  m_SphereRadius: 0.5\n"
    "  m_MinimumSkinnedVertexWeight: 0.1\n"
    "  m_ConvexHullGenerationParameters:\n"
    "    m_SimplificationTolerance: 0.015\n"
    "    m_BevelRadius: 0\n"
    "    m_MinimumAngle: 2.5000002\n"
    "  m_CustomMesh: {fileID: 0}\n"
    "  m_ForceUnique: 0\n"
    "  m_Material:\n"
    "    m_SupportsTemplate: 1\n"
    "    m_Template: {fileID: 0}\n"
    "    m_CollisionResponse:\n      m_Override: 0\n      m_Value: 0\n"
    "    m_Friction:\n      m_Override: 0\n      m_Value:\n        Value: 0.5\n        CombineMode: 0\n"
    "    m_Restitution:\n      m_Override: 0\n      m_Value:\n        Value: 0\n        CombineMode: 2\n"
    "    m_BelongsToCategories:\n      m_Override: 0\n      m_Value:\n"
    + "".join(f"        Category{i:02d}: {1 if i == 0 else 0}\n" for i in range(32))
    + "    m_CollidesWithCategories:\n      m_Override: 0\n      m_Value:\n"
    + "".join(f"        Category{i:02d}: {1 if i in (0, 2, 4) else 0}\n" for i in range(32))
    + "    m_CustomMaterialTags:\n      m_Override: 0\n      m_Value:\n"
    + "".join(f"        Tag{i:02d}: 0\n" for i in range(8))
    + "    m_SerializedVersion: 1\n"
    "  m_SerializedVersion: 1\n"
)


def _crafting_authoring_body(spec: ObjectSpec) -> str:
    """CraftingAuthoring — the list of things this station makes.

    Modded objects go in by name (moddedObjectID) with objectID left at 0, because a mod's numeric
    id does not exist until the game assigns one at load. That is how the SDK example refers to its
    own items, and it is why the pylon can be listed here without any id plumbing.
    """
    entries = "".join(
        "  - objectID: 0\n"
        f"    moddedObjectID: {name}\n"
        "    amount: 1\n"
        "    craftingConsumesEntityAmount: 0\n"
        "    entityAmountToConsume: 0\n"
        "    allowCraftingNone: 0\n"
        f"    craftingTime: {spec.crafting_time}\n"
        "    hasPrerequisites: 0\n"
        "    prerequisites:\n"
        "      contentBundlePresent:\n"
        "        hasValue: 0\n"
        "        value:\n" + _null_address("          ")
        + "      contentBundleAbsent:\n"
        "        hasValue: 0\n"
        "        value:\n" + _null_address("          ")
        + "      birdBossKilled: 0\n"
        "      octopusBossKilled: 0\n"
        "      scarabBossKilled: 0\n"
        "      hydraBossNatureKilled: 0\n"
        "      hydraBossSeaKilled: 0\n"
        "      hydraBossDesertKilled: 0\n"
        for name in spec.crafts
    )
    return (
        "  craftingType: 0\n"
        "  showLoopEffectOnOutputSlot: 0\n"
        "  allInventoryIsForSingleCraft: 0\n"
        "  canCraftObjects:\n" + entries
        + "  includeCraftedObjectsFromBuildings: []\n"
        "  extractableType: 0\n"
        "  minMaxRandomDefaultExtractedOutputAmount: {x: 0, y: 0}\n"
        "  minMaxRandomDefaultCraftingTime: {x: 0, y: 0}\n"
    )


def logic_prefab(spec: ObjectSpec) -> str:
    """The ECS side: what the object IS.

    A placeable follows the SDK workbench, minus RotationAuthoring (nothing we make turns to face
    the player) and with CraftingAuthoring only on stations. An item stops after the three the SDK's
    Sword1 carries — everything below them describes a thing that exists in the world, which an item
    in a bag does not.
    """
    path = spec.logic_path
    fid = lambda node: local_file_id(path, node)  # noqa: E731

    root = fid("root")
    transform = fid("transform")
    parts = [
        ("object", None),
        ("item", None),
    ]
    if spec.crafts:
        parts.append(("crafting", None))
    if spec.is_placeable:
        parts += [
            ("mineable", None),
            ("health", None),
            ("placeable", None),
            ("ignoreVertexOffsets", None),
            ("state", None),
            ("idleState", None),
            ("tookDamageState", None),
            ("deathState", None),
            ("damageReduction", None),
        ]
    if spec.is_placeable and spec.interact_method:
        parts.append(("interactable", None))
    parts.append(("localization", None))
    if spec.is_placeable:
        parts += [
            ("animSupport", None),
            ("physicsShape", None),
            ("ghost", None),
            ("marker", None),
        ]
    ids = {name: fid(name) for name, _ in parts}

    recipe_block = "  requiredObjectsToCraft:" + (
        " []\n"
        if not spec.recipe
        else "\n" + "".join(
            f"  - objectName: {name}\n    amount: {amount}\n" for name, amount in spec.recipe
        )
    )
    icon = f"{{fileID: {FID_SPRITE}, guid: {asset_guid(spec.texture_path)}, type: 3}}"
    graphics_root = local_file_id(spec.graphics_path, "root")

    body = YAML_HEADER
    body += _game_object(root, spec.key, [transform] + [ids[n] for n, _ in parts])
    body += _transform(transform, root, 0)

    body += _authoring(
        ids["object"], root, "ObjectAuthoring",
        f"  objectName: {spec.object_name}\n"
        "  initialAmount: 1\n"
        # 기획서 §5: a freshly placed pylon starts off, which is variation 0. The two fields below
        # declare that this object's variation changes at runtime and what it toggles between.
        "  variation: 0\n"
        f"  variationIsDynamic: {1 if spec.variation_is_dynamic else 0}\n"
        f"  variationToToggleTo: {spec.variation_to_toggle_to}\n"
        f"  objectType: {spec.object_type}\n"
        f"  tags: {TAG_CAN_BE_SALVAGED}\n"
        f"  rarity: {spec.rarity}\n"
        "  salvageMultiplier: 1\n"
        # An item has no presence in the world, so no graphics prefab — the SDK's Sword1 leaves this
        # at 0 the same way.
        + (f"  graphicalPrefab: {{fileID: {graphics_root}, "
           f"guid: {asset_guid(spec.graphics_path)}, type: 3}}\n"
           if spec.is_placeable else "  graphicalPrefab: {fileID: 0}\n")
        + "  isCustomScenePrefab: 0\n"
        "  additionalSprites: []\n",
    )
    body += _authoring(
        ids["item"], root, "InventoryItemAuthoring",
        "  sellValue: -1\n"
        "  buyValueMultiplier: 1\n"
        f"  icon: {icon}\n"
        "  iconOffset: {x: 0, y: 0}\n"
        f"  smallIcon: {icon}\n"
        f"  isStackable: {1 if spec.stackable else 0}\n"
        + recipe_block
        + f"  craftingTime: {spec.crafting_time}\n",
    )
    if spec.crafts:
        body += _authoring(ids["crafting"], root, "CraftingAuthoring",
                           _crafting_authoring_body(spec))
    if not spec.is_placeable:
        # Everything past here describes something standing in the world. An item ends with its
        # name, exactly as the SDK's Sword1 does.
        body += _authoring(ids["localization"], root, "LocalizationAuthoring",
                           f"  termKey: {spec.object_name}\n  languageGenders: []\n")
        return body

    body += _authoring(
        ids["mineable"], root, "MineableAuthoring",
        "  playFailedEffectOnZeroDamage: 0\n",
    )
    body += _authoring(
        ids["health"], root, "HealthAuthoring",
        "  dontCalculateHealthFromLevel: 0\n"
        "  overrideStartHealth: 0\n"
        "  normalizedOverrideStartHealth: 1\n"
        f"  startHealth: {spec.health}\n"
        f"  maxHealth: {spec.health}\n"
        "  maxHealthMultiplier: 1\n"
        "  hasHealthRegeneration: 1\n"
        "  healInCombatAsWell: 0\n"
        "  healthIncreasePercentPerFiveSeconds: 100\n"
        "  healDelayAfterLeavingCombat: 5\n"
        "  level: {fileID: 0}\n",
    )
    body += _authoring(
        ids["placeable"], root, "PlaceableObjectAuthoring",
        f"  prefabTileSize: {{x: {spec.tile_size[0]}, y: {spec.tile_size[1]}}}\n"
        "  prefabCornerOffset: {x: 0, y: 0}\n"
        "  centerIsAtEntityPosition: 0\n"
        "  objectCanBeToggledToNewNonRotationOption: 0\n"
        "  toggledToNewNonRotationOptions: 0\n"
        "  variationToPlace: 0\n"
        "  canBePlacedOnPlayer: 0\n"
        "  canBePlacedOnAnyWalkableTile: 1\n"
        "  canBePlacedOnWater: 0\n"
        "  canBePlacedOnLava: 0\n"
        "  canBePlacedOnPit: 0\n"
        "  canBePlacedOnBlockingObjects: 0\n"
        "  canBePlacedOnLowColliders: 0\n"
        "  dontDestroyObjectIfInvalidPlacement: 0\n"
        "  canBePlacedOnImmuneTiles: 0\n"
        "  dontBlockRoots: 0\n"
        "  canBePlacedOnObjects: \n"
        "  canNotBePlacedOnObjects: \n"
        "  hasVariationsThatCanBePlacedOnWalls: 0\n"
        "  canPlaceOnSideOfWall: 0\n"
        "  wallSideVariationStartsOnIndex1: 0\n"
        "  blocksHangingWallObjects: 0\n"
        "  appearInMapUI: 0\n"
        "  mapColor: {r: 0, g: 0, b: 0, a: 0}\n"
        "  alignWithPlayerDirection: 0\n"
        "  displayPlaceableType: 0\n",
    )
    body += _authoring(ids["ignoreVertexOffsets"], root, "IgnoreVertexOffsetsAuthoring")
    body += _authoring(ids["state"], root, "StateAuthoring")
    body += _authoring(ids["idleState"], root, "IdleStateAuthoring", "  playIdleAnimation: 1\n")
    body += _authoring(
        ids["tookDamageState"], root, "TookDamageStateAuthoring",
        "  duration: 0\n  refreshStateOnNewDamageTaken: 0\n",
    )
    body += _authoring(
        ids["deathState"], root, "DeathStateAuthoring",
        "  overrideTimeBeforeDestroy: 0\n"
        "  timeBeforeDestroy: 0\n"
        "  timeBeforeLootDrop: 0\n"
        "  skipDeathAnimation: 0\n",
    )
    body += _authoring(
        ids["damageReduction"], root, "DamageReductionAuthoring",
        "  calculateReductionFromLevel: 0\n"
        "  reductionMultiplier: 1\n"
        "  reduction: 0\n"
        "  maxDamagePerHit: 1\n"
        "  minDamagePerHit: 0\n"
        "  ignoreReductionWhenDamagedByDrill: 0\n"
        "  level: {fileID: 0}\n",
    )
    if spec.interact_method:
        # THE ECS HALF OF "E DOES SOMETHING". The graphics prefab's InteractableObject only says
        # which method to call; this is what puts the entity on the interaction path at all, and
        # without it the workbench and the pylon both ignored E entirely. Found by diffing this
        # prefab's components against the SDK's working workbench: it and RotationAuthoring (which
        # we drop on purpose, nothing of ours turns) were the only two it had and we did not.
        #
        # useSecondInteraction stays 0: that is right-click, and the remote reaches a pylon through
        # ClientInput rather than through the pylon's own interactable (research.md 15장).
        body += _authoring(ids["interactable"], root, "Interaction.LocalInteractableAuthoring",
                           "  useSecondInteraction: 0\n  interactSubIndex: 0\n")
    body += _authoring(ids["localization"], root, "LocalizationAuthoring",
                       f"  termKey: {spec.object_name}\n  languageGenders: []\n")
    body += _behaviour(ids["animSupport"], root, *game_script("AnimationAuthoring"),
                       "  orientationSupport: 0\n  largeAnimationHistorySupport: 0\n")
    body += _behaviour(ids["physicsShape"], root, *game_script("Unity.Physics.Authoring.PhysicsShapeAuthoring"),
                       PHYSICS_SHAPE_BODY)
    body += _behaviour(
        ids["ghost"], root, *game_script("Unity.NetCode.GhostAuthoringComponent"),
        "  DefaultGhostMode: 0\n"
        "  SupportedGhostModes: 3\n"
        "  OptimizationMode: 1\n"
        "  Importance: 1\n"
        # Both references set prefabId to the logic prefab's own guid.
        f"  prefabId: {asset_guid(path)}\n"
        "  HasOwner: 0\n"
        "  SupportAutoCommandTarget: 0\n"
        "  TrackInterpolationDelay: 0\n"
        "  GhostGroup: 0\n"
        "  UsePreSerialization: 0\n"
        "  DontUsePredictionBackup: 0\n",
    )
    body += _behaviour(ids["marker"], root, *game_script("Unity.Entities.Hybrid.Baking.LinkedEntityGroupAuthoring"))
    return body


def _unity_event(target: int, type_name: str, method: str) -> str:
    """One persistent UnityEvent call, as the inspector serialises it.

    m_TargetAssemblyTypeName is a plain string Unity resolves by name, so preflight cannot check it
    and a typo shows up only as an interaction that silently does nothing. m_Mode 1 is "no argument"
    and m_CallState 2 is "run in play mode and at runtime" — both copied from the SDK workbench.
    """
    return (
        "  - m_PersistentCalls:\n"
        "      m_Calls:\n"
        f"      - m_Target: {{fileID: {target}}}\n"
        f"        m_TargetAssemblyTypeName: {type_name}, {MOD_ASSEMBLY}\n"
        f"        m_MethodName: {method}\n"
        "        m_Mode: 1\n"
        "        m_Arguments:\n"
        "          m_ObjectArgument: {fileID: 0}\n"
        "          m_ObjectArgumentAssemblyTypeName: UnityEngine.Object, UnityEngine\n"
        "          m_IntArgument: 0\n"
        "          m_FloatArgument: 0\n"
        "          m_StringArgument: \n"
        "          m_BoolArgument: 0\n"
        "        m_CallState: 2\n"
    )


def graphics_prefab(spec: ObjectSpec) -> str:
    """The rendering side: root -> XScaler -> SpriteObject, plus an Interactable on a station.

    Structure follows ck-mods ConveyorTunnelVisual (the minimal working shape) with the field set of
    the newer SDK example. A plain placeable uses the stock EntityMonoBehaviour as its root; a
    station points at one of our own scripts instead, because the root has to derive from
    CraftingBuilding for the crafting UI to open.

    WHY A SUBCLASS RATHER THAN STOCK CraftingBuilding: a prefab names a game class in one of two
    ways — {fileID: 11500000, guid: <that .cs's meta guid>} for a loose script, or
    {fileID: <hash of the class name>, guid: <assembly>} for one compiled into a dll. Both forms
    appear for Pug.Other classes (EntityMonoBehaviour is the first, InteractableObject the second)
    and no reference prefab uses stock CraftingBuilding, so which one it wants is unknown. Pointing
    at our own script sidesteps the question: we own that .cs, so it is the first form by
    construction. The SDK example subclasses it too.
    """
    path = spec.graphics_path
    fid = lambda node: local_file_id(path, node)  # noqa: E731

    root, root_tf, emb = fid("root"), fid("rootTransform"), fid("entityMonoBehaviour")
    scaler, scaler_tf = fid("xscaler"), fid("xscalerTransform")
    sprite, sprite_tf, sprite_obj = fid("sprite"), fid("spriteTransform"), fid("spriteObject")
    interactable_go, interactable_tf = fid("interactable"), fid("interactableTransform")
    interactable = fid("interactableObject")
    low, high = data_block_address(spec.sprite_asset_path)

    # Two independent traits: anything with a method to call gets an InteractableObject, but only a
    # crafting station also carries CraftingBuilding's fields. The pylon is the first object that is
    # one without the other.
    is_interactive = bool(spec.interact_method)
    is_station = bool(spec.crafts)
    root_children = [scaler_tf] + ([interactable_tf] if is_interactive else [])
    root_script = (
        (FID_MONOSCRIPT_CS, asset_guid(spec.graphics_script))
        if spec.graphics_script
        else game_script("EntityMonoBehaviour")
    )

    # CraftingBuilding's own serialized fields sit after EntityMonoBehaviour's, in this order
    # (ck-db Pug.Other/CraftingBuilding.cs:234-251). An empty title list would leave the crafting
    # window unlabelled, so the SDK's three terms are reused.
    titles = "".join(
        f"    - mTerm: {term}\n"
        "      mRTL_IgnoreArabicFix: 0\n"
        "      mRTL_MaxLineLength: 0\n"
        "      mRTL_ConvertNumbers: 0\n"
        "      m_DontLocalizeParameters: 0\n"
        for term in spec.ui_titles
    )
    crafting_fields = (
        "  hideRecipes: 0\n"
        "  electricitySprite: {fileID: 0}\n"  # 기획서 §5: no power needed
        "  defaultUISettings:\n"
        + ("    titles: []\n" if not titles else "    titles:\n" + titles)
        + "    craftingUIBackgroundVariation: 0\n"
        "  buildingSpecificUISettings: []\n"
        "  craftingCategoryWindowInfos: []\n"
    ) if is_station else ""

    body = YAML_HEADER
    body += _game_object(root, f"{spec.key}Graphics", [root_tf, emb])
    body += _transform(root_tf, root, 0, children=root_children)
    body += _behaviour(
        emb, root, root_script[0], root_script[1],
        f"  XScaler: {{fileID: {scaler_tf}}}\n"
        "  shadow: {fileID: 0}\n"
        "  indirectLightEmitters: []\n"
        "  animator: {fileID: 0}\n"
        + (f"  interactable: {{fileID: {interactable}}}\n" if is_interactive
           else "  interactable: {fileID: 0}\n")
        + "  spriteObjects:\n"
        f"  - {{fileID: {sprite_obj}}}\n"
        "  useSharedTransformAnimations: 1\n"
        "  reskinOptions: []\n"
        "  paintableOptions:\n"
        "    spriteRenderers: []\n"
        "    spriteColorTints: []\n"
        "    spriteSheetSkins: []\n"
        "  soundOptions:\n"
        "    takeDamageSfx:\n      value: 0\n"
        "    deathSfx:\n      value: 0\n"
        "    soundsToPlay: []\n"
        "  particleOptions:\n"
        "    particlesToSpawn: []\n"
        "    particleSpawnLocations: []\n"
        "    particlesToDisableOnLowQuality: []\n"
        "  objectVariants: []\n"
        "  spritesToRandomlyFlip: []\n"
        "  gameObjectsToRandomlyFlip: []\n"
        "  optionalHealthBar: {fileID: 0}\n"
        "  optionalLightOptimizer: {fileID: 0}\n"
        "  conditionEffectsHandler: {fileID: 0}\n"
        "  conditionsEffectsSettings:\n"
        "    stunEffectOffset: {x: 0, y: 0, z: 0}\n"
        "  outlineControllers: []\n"
        "  hasDisableableParticles: 0\n"
        "  previousHealth: 0\n"
        "  m_spriteObjectOrientationHash: 0\n"
        + crafting_fields,
    )
    body += _game_object(scaler, "XScaler", [scaler_tf])
    body += _transform(scaler_tf, scaler, root_tf, children=[sprite_tf])
    body += _game_object(sprite, "SpriteObject", [sprite_tf, sprite_obj])
    body += _transform(sprite_tf, sprite, scaler_tf, position=spec.sprite_offset)
    body += _behaviour(
        sprite_obj, sprite, *game_script("Pug.Sprite.SpriteObject"),
        # This address, not a guid, is how the SpriteObject finds its SpriteAsset (research.md 11장).
        "  m_assetRef:\n" + _address("    ", low, high)
        + "  skinRef:\n" + _null_address("    ")
        + "  color: {r: 1, g: 1, b: 1, a: 1}\n"
        "  emissiveColor: {r: 1, g: 1, b: 1, a: 1}\n"
        "  flashColor: {r: 0, g: 0, b: 0, a: 0}\n"
        "  outlineColor: {r: 1, g: 1, b: 1, a: 0}\n"
        + "  primaryGradientMap:\n" + _null_address("    ")
        + "  secondaryGradientMap:\n" + _null_address("    ")
        + "  tertiaryGradientMap:\n" + _null_address("    ")
        + "  primaryGradientMapRef:\n" + _null_address("    ")
        + "  secondaryGradientMapRef:\n" + _null_address("    ")
        + "  tertiaryGradientMapRef:\n" + _null_address("    ")
        + f"  material: {{fileID: 2100000, guid: {GUID_SPRITE_MATERIAL}, type: 2}}\n"
        "  m_startAnimationHash: 0\n"
        "  m_startVariantHash: 0\n"
        "  animationTimescale: 1\n"
        "  m_randomStartTime: 0\n"
        "  processAnimationEvents: 0\n"
        "  syncSource: {fileID: 0}\n"
        "  syncAnimation: 1\n"
        "  syncVariant: 1\n"
        "  syncSprite: 0\n"
        "  maskChannel: 0\n"
        "  maskInteraction: 0\n",
    )

    if is_interactive:
        body += _game_object(interactable_go, "Interactable", [interactable_tf, interactable])
        body += _transform(interactable_tf, interactable_go, root_tf)
        body += _behaviour(
            interactable, interactable_go, *game_script("InteractableObject"),
            "  weightMultiplier: 1\n"
            "  useDiscreteOutlineColor: 0\n"
            "  requiredFactionToInteract: 0\n"
            "  optionalIcon: {fileID: 0}\n"
            "  optionalOutlineController: {fileID: 0}\n"
            "  onUseActions:\n"
            + _unity_event(emb, spec.graphics_class, spec.interact_method)
            # Only a crafting station needs the leave hook — it closes the window the player opened.
            # A pylon's toggle has nothing to undo when they walk off.
            + ("  onTriggerExitActions: []\n" if not is_station else
               "  onTriggerExitActions:\n"
               + _unity_event(emb, spec.graphics_class, "OnPlayerLeftBuilding"))
            + "  radius: 2\n"
            "  subInteractingData: []\n"
            "  allowToUseOnlyWhenClaimed: 0\n"
            "  ignorePlayerDirection: 0\n",
        )

    return body


# ---------------------------------------------------------------------------------------------
# Driver
# ---------------------------------------------------------------------------------------------

def build_outputs():
    """Files this generator owns and rewrites every run, as {repo-relative path: bytes}."""
    out = {}

    for spec in SPECS:
        out[spec.texture_path] = (REPO / spec.art).read_bytes()
        out[spec.texture_path + ".meta"] = texture_meta(spec.texture_path, spec.pixels_to_units)
        for suffix, lit_art in spec.variants:
            variant = spec.variant_texture_path(suffix)
            check_same_silhouette(REPO / spec.art, REPO / lit_art)
            out[variant] = (REPO / lit_art).read_bytes()
            out[variant + ".meta"] = texture_meta(variant, spec.pixels_to_units)
        out[spec.text_path] = text_data_block(spec)
        out[spec.text_path + ".meta"] = asset_meta(spec.text_path)
        out[spec.logic_path] = logic_prefab(spec)
        out[spec.logic_path + ".meta"] = prefab_meta(spec.logic_path)

        # A SpriteAsset exists to draw something in the world, and a graphics prefab to hold it.
        # An item has neither: its icon points straight at the PNG's sprite, the way Sword1's does.
        if spec.is_placeable:
            out[spec.sprite_asset_path] = sprite_asset(spec)
            out[spec.sprite_asset_path + ".meta"] = asset_meta(spec.sprite_asset_path)
            out[spec.graphics_path] = graphics_prefab(spec)
            out[spec.graphics_path + ".meta"] = prefab_meta(spec.graphics_path)

    # The lens's outline marker. Not attached to any spec: it is never an object in the world, just
    # a sprite the overlay stamps on tiles. NoBreakZoneRangeOverlay finds it by this file's name,
    # the way the reference mod picks up its own helper sprites in ModObjectLoaded.
    out[RANGE_MARKER_TEXTURE] = range_marker_png()
    out[RANGE_MARKER_TEXTURE + ".meta"] = texture_meta(RANGE_MARKER_TEXTURE, MARKER_TILE_PIXELS)

    out["SpriteAssetManifest.asset"] = sprite_asset_manifest(
        [s for s in SPECS if s.is_placeable])
    out["SpriteAssetManifest.asset.meta"] = asset_meta("SpriteAssetManifest.asset")

    return {k: v if isinstance(v, bytes) else v.encode("utf-8") for k, v in sorted(out.items())}


def build_folder_metas(owned):
    """.meta for each folder the generated assets live in — CREATED ONCE, NEVER REWRITTEN.

    Unity issues a folder a guid too, and a folder that loses its .meta takes every asset under it
    with it (CLAUDE.md §1-2). A folder that already has one (Data/ predates this script) keeps the
    guid Unity gave it; regenerating that would be exactly the breakage the rule warns about.
    """
    folders = set()
    for path in owned:
        parent = pathlib.PurePosixPath(path).parent
        while str(parent) != ".":
            folders.add(str(parent))
            parent = parent.parent
    return {f + ".meta": folder_meta(f).encode("utf-8") for f in sorted(folders)}


def check_referenced_scripts():
    """A prefab pointing at one of our scripts must use that file's real guid.

    Scripts live under Scripts/ and are not generated here, so their .meta is written by hand (or by
    Unity) while the prefab reference is computed. If the two drift the component silently vanishes
    from the prefab, which is the sort of thing that only shows up as "the workbench does not open".
    """
    problems = []
    for spec in SPECS:
        if not spec.graphics_script:
            continue

        source = REPO / spec.graphics_script
        meta = REPO / (spec.graphics_script + ".meta")
        if not source.exists():
            problems.append(f"{spec.graphics_script}: referenced by {spec.key} but does not exist")
            continue
        if not meta.exists():
            problems.append(f"{spec.graphics_script}: missing .meta")
            continue

        found = re.search(r"^guid: (\w+)", meta.read_text(encoding="utf-8"), re.MULTILINE)
        expected = asset_guid(spec.graphics_script)
        if not found or found.group(1) != expected:
            problems.append(
                f"{spec.graphics_script}.meta: guid is {found.group(1) if found else 'missing'}, "
                f"but prefabs reference {expected}"
            )
    return problems


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true",
                        help="report stale files and exit non-zero instead of writing")
    args = parser.parse_args()

    script_problems = check_referenced_scripts()
    if script_problems:
        print(f"FAIL — {len(script_problems)} problem(s) before generating:")
        for problem in script_problems:
            print(f"  - {problem}")
        return 1

    outputs = build_outputs()
    written, unchanged, stale = [], [], []

    for rel, content in outputs.items():
        path = REPO / rel
        current = path.read_bytes() if path.exists() else None
        if current == content:
            unchanged.append(rel)
            continue
        if args.check:
            stale.append(rel)
            continue
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(content)
        written.append(rel)

    for rel, content in build_folder_metas(outputs).items():
        path = REPO / rel
        if path.exists():
            unchanged.append(rel)
            continue
        if args.check:
            stale.append(rel)
            continue
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(content)
        written.append(rel)

    if args.check:
        print(f"  {len(unchanged):5}  up to date")
        if stale:
            print(f"\nFAIL — {len(stale)} file(s) differ from the spec:")
            for rel in stale:
                print(f"  - {rel}")
            return 1
        print("\nOK — every generated asset matches Editor/genassets.py")
        return 0

    for rel in written:
        print(f"  write    {rel}")
    print(f"\n  {len(written):5}  written\n  {len(unchanged):5}  unchanged")
    print("\nRun python3 Editor/preflight.py next — it reverses the m_Script references emitted here.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
