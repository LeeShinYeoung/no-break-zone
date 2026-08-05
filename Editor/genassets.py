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
import hashlib
import pathlib
import struct
import sys

# Importing a sibling would drop a __pycache__ inside the mod path, which then trips preflight's
# .meta check and would need a .gitignore entry to stay out of the repo. Cheaper to not create it.
sys.dont_write_bytecode = True
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
from preflight import script_file_id  # noqa: E402  (same MD4 the prefab checker reverses)

REPO = pathlib.Path(__file__).resolve().parent.parent


# ---------------------------------------------------------------------------------------------
# Constants observed in reference assets. Nothing here is invented; each line says where it came
# from so a future game update can be re-checked against the same file.
# ---------------------------------------------------------------------------------------------

# Assembly guids, as they appear in reference prefabs. NOTE (research.md 11장): the guids in the
# SDK's MetaFiles.zip do NOT match these — trust the prefabs.
GUID_AUTHORING = "3392f4c23e1d8662d749dabb2361ee02"  # Pug.ECS.Authoring — 184 refs, all resolved
GUID_PUGSPRITE = "292700ef68995bdb2163e35989fc7eb0"  # PugSprite: SpriteObject/SpriteAsset/Manifest
GUID_ENTITY_MB = "6f4e9f12d8be4d048a7b574866c31a4f"  # EntityMonoBehaviour (ck-mods ConveyorTunnelVisual)
GUID_TEXT_BLOCK = "e853a5af7d19630282ad0af7b5dabadc"  # TextDataBlock
GUID_PHYSICS_SHAPE = "b275e5f92732148048d7b77e264ac30e"  # Unity.Physics PhysicsShapeAuthoring
GUID_GHOST = "7c79d771cedb4794bf100ce60df5f764"  # NetCode GhostAuthoringComponent
GUID_ANIM_SUPPORT = "f8b40f5d7f8d7c18ee25ec4e04143ad5"  # orientation/animation support (both refs)
GUID_EMPTY_MARKER = "c16549610bfe4458aa9389201d072bb6"  # fieldless marker present on both refs
GUID_SPRITE_MATERIAL = "571bf3c761ee86c4f9d8e65be27151be"  # SpriteObject.material, both refs

# fileIDs that are not derived from a class name.
FID_MONOSCRIPT_CS = 11500000  # class lives in a .cs, not a .dll
FID_SCRIPTABLE_OBJECT = 11400000  # the single object inside a .asset
FID_TEXTURE2D = 2800000  # Texture2D subasset — what SpriteAsset.texture points at
FID_SPRITE = 21300000  # Sprite subasset of a spriteMode:1 texture — what an item icon points at
FID_SPRITE_OBJECT = 1908045241
FID_SPRITE_ASSET = -217761678
FID_SPRITE_MANIFEST = 1876717734
FID_TEXT_DATA_BLOCK = 2108018792

OBJECT_TYPE_PLACEABLE_PREFAB = 800  # ObjectType.PlaceablePrefab
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


def data_block_address(key: str) -> tuple:
    """(m_low, m_high) for a DataBlockAddress — how sprites and text are linked (research.md 11장).

    UNVERIFIED ASSUMPTION: we never found what the game derives m_address from, and the SDK's values
    match neither the file guid nor a name hash. We treat it as an id the asset declares about
    itself, which only requires that it be unique and stable. If the pylon has no sprite in game,
    suspect this first.
    """
    low, high = struct.unpack("<qq", _digest("address", key))
    return low, high


# ---------------------------------------------------------------------------------------------
# The spec. 6단계 adds lens/remote/workbench by appending here, not by writing YAML.
# ---------------------------------------------------------------------------------------------

class Placeable:
    """One placeable object: logic prefab + graphics prefab + sprite + text + texture import."""

    def __init__(self, key, object_name, title, description, art,
                 tile_size=(1, 1), pixels_to_units=16, stackable=True, rarity=3,
                 health=10, recipe=(), crafting_time=3.0,
                 sprite_offset=(0, 0.0625, -0.3125)):
        self.key = key  # asset base name; also the localization termKey
        self.object_name = object_name  # ObjectID string — 기획서 §4, never change (CLAUDE.md §5)
        self.title = title
        self.description = description
        self.art = art  # source PNG under Editor/Docs/art
        self.tile_size = tile_size
        self.pixels_to_units = pixels_to_units
        self.stackable = stackable
        self.rarity = rarity
        self.health = health
        self.recipe = list(recipe)  # [(objectName, amount)] — where it is craftable is 6단계
        self.crafting_time = crafting_time
        # Nudge of the sprite quad relative to the object. Copied from the SDK workbench, whose art
        # is 16x18 rather than our size, so this is a starting point to eyeball at 체크포인트 1 —
        # not a value anybody verified for this sprite.
        self.sprite_offset = sprite_offset

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
        return f"Data/TextDataBlock/Items/{self.key}.asset"

    @property
    def logic_path(self):
        return f"Prefabs/{self.key}.prefab"

    @property
    def graphics_path(self):
        return f"Prefabs/{self.key}Graphics.prefab"


SPECS = [
    Placeable(
        key="NoBreakZonePylon",
        object_name="NoBreakZone.Pylon",  # 기획서 §4. Written into saves — changing it breaks them.
        title="No Break Pylon",
        description="Protects nearby objects. While it is on, nothing inside can be destroyed.",
        art="Editor/Docs/art/pylon_off.png",
        # 기획서 §4: 1x1 tiles. The draft art is 32px where a tile is 16px; see the note in
        # status.md about keeping it for now and looking at it in game first.
        tile_size=(1, 1),
        pixels_to_units=32,
        stackable=True,
        rarity=3,
        health=10,
        # 기획서 §4: iron + ancient gemstone + mechanical part. Nothing lists the pylon as craftable
        # until the workbench exists (6단계), so this recipe is inert for now.
        recipe=[("IronBar", 8), ("AncientGemstone", 1), ("MechanicalPart", 2)],
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


def sprite_asset(spec: Placeable) -> str:
    """SpriteAsset: the thing a graphics prefab's SpriteObject resolves through its m_address.

    m_staticSpriteData is variation 0. 4단계's on/off toggle adds variation 1 to m_staticVariants,
    and 기획서 §7's glow goes in emissiveTexture rather than a separate sprite (research.md 11장).
    """
    low, high = data_block_address(spec.sprite_asset_path)
    texture_guid = asset_guid(spec.texture_path)
    return (
        _scriptable_header(spec.key, FID_SPRITE_ASSET, GUID_PUGSPRITE)
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
        "  m_staticVariants: []\n"
        "  m_animations: []\n"
        "  m_events: []\n"
        "  m_positionalData: []\n"
        "  references:\n"
        "    version: 2\n"
        "    RefIds: []\n"
    )


def text_data_block(spec: Placeable) -> str:
    """Item name and description. LocalizationAuthoring.termKey on the logic prefab points here."""
    low, high = data_block_address(spec.text_path)
    keys = "".join(f"    - m_low: {lo}\n      m_high: {hi}\n" for lo, hi in LANGUAGE_ADDRESSES)
    values = "".join(
        "    - m_language:\n"
        + _address("        ", lo, hi)
        + f"      title: {spec.title}\n"
        f"      description: {spec.description}\n"
        for lo, hi in LANGUAGE_ADDRESSES
    )
    primary_low, primary_high = LANGUAGE_ADDRESSES[PRIMARY_LANGUAGE_INDEX]
    return (
        _scriptable_header(spec.key, FID_TEXT_DATA_BLOCK, GUID_TEXT_BLOCK)
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
        _scriptable_header("SpriteAssetManifest", FID_SPRITE_MANIFEST, GUID_PUGSPRITE)
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
    """A Pug.ECS.Authoring component, addressed by class name rather than a copied number.

    This is the check preflight.py reverses: a misspelled class here produces a fileID that maps to
    no game class and fails on the Mac.
    """
    return _behaviour(fid, owner, script_file_id(class_name), GUID_AUTHORING, body)


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


def logic_prefab(spec: Placeable) -> str:
    """The ECS side: what the object IS. Component set follows the SDK workbench minus the parts
    that only make sense for a crafting station (CraftingAuthoring) or a rotating object
    (RotationAuthoring)."""
    path = spec.logic_path
    fid = lambda node: local_file_id(path, node)  # noqa: E731

    root = fid("root")
    transform = fid("transform")
    parts = [
        ("object", None),
        ("item", None),
        ("mineable", None),
        ("health", None),
        ("placeable", None),
        ("ignoreVertexOffsets", None),
        ("state", None),
        ("idleState", None),
        ("tookDamageState", None),
        ("deathState", None),
        ("damageReduction", None),
        ("localization", None),
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
        # 4단계 turns these three into the on/off toggle: variationIsDynamic 1, toggle 0 <-> 1.
        # 기획서 §5 says a freshly placed pylon starts off, which is variation 0.
        "  variation: 0\n"
        "  variationIsDynamic: 0\n"
        "  variationToToggleTo: 0\n"
        f"  objectType: {OBJECT_TYPE_PLACEABLE_PREFAB}\n"
        f"  tags: {TAG_CAN_BE_SALVAGED}\n"
        f"  rarity: {spec.rarity}\n"
        "  salvageMultiplier: 1\n"
        f"  graphicalPrefab: {{fileID: {graphics_root}, guid: {asset_guid(spec.graphics_path)}, type: 3}}\n"
        "  isCustomScenePrefab: 0\n"
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
    body += _authoring(ids["localization"], root, "LocalizationAuthoring",
                       f"  termKey: {spec.key}\n  languageGenders: []\n")
    body += _behaviour(ids["animSupport"], root, 775935493, GUID_ANIM_SUPPORT,
                       "  orientationSupport: 0\n  largeAnimationHistorySupport: 0\n")
    body += _behaviour(ids["physicsShape"], root, FID_MONOSCRIPT_CS, GUID_PHYSICS_SHAPE,
                       PHYSICS_SHAPE_BODY)
    body += _behaviour(
        ids["ghost"], root, FID_MONOSCRIPT_CS, GUID_GHOST,
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
    body += _behaviour(ids["marker"], root, FID_MONOSCRIPT_CS, GUID_EMPTY_MARKER)
    return body


def graphics_prefab(spec: Placeable) -> str:
    """The rendering side: root EntityMonoBehaviour -> XScaler -> SpriteObject.

    Structure follows ck-mods ConveyorTunnelVisual (the minimal working shape) with the field set of
    the newer SDK example. No subclass script is needed — the stock EntityMonoBehaviour is enough
    until 5단계 adds glow and 4단계 adds interaction.
    """
    path = spec.graphics_path
    fid = lambda node: local_file_id(path, node)  # noqa: E731

    root, root_tf, emb = fid("root"), fid("rootTransform"), fid("entityMonoBehaviour")
    scaler, scaler_tf = fid("xscaler"), fid("xscalerTransform")
    sprite, sprite_tf, sprite_obj = fid("sprite"), fid("spriteTransform"), fid("spriteObject")
    low, high = data_block_address(spec.sprite_asset_path)

    body = YAML_HEADER
    body += _game_object(root, f"{spec.key}Graphics", [root_tf, emb])
    body += _transform(root_tf, root, 0, children=[scaler_tf])
    body += _behaviour(
        emb, root, FID_MONOSCRIPT_CS, GUID_ENTITY_MB,
        f"  XScaler: {{fileID: {scaler_tf}}}\n"
        "  shadow: {fileID: 0}\n"
        "  indirectLightEmitters: []\n"
        "  animator: {fileID: 0}\n"
        # 4단계 hangs an InteractableObject here for the E-key toggle.
        "  interactable: {fileID: 0}\n"
        "  spriteObjects:\n"
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
        "  m_spriteObjectOrientationHash: 0\n",
    )
    body += _game_object(scaler, "XScaler", [scaler_tf])
    body += _transform(scaler_tf, scaler, root_tf, children=[sprite_tf])
    body += _game_object(sprite, "SpriteObject", [sprite_tf, sprite_obj])
    body += _transform(sprite_tf, sprite, scaler_tf, position=spec.sprite_offset)
    body += _behaviour(
        sprite_obj, sprite, FID_SPRITE_OBJECT, GUID_PUGSPRITE,
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
        out[spec.sprite_asset_path] = sprite_asset(spec)
        out[spec.sprite_asset_path + ".meta"] = asset_meta(spec.sprite_asset_path)
        out[spec.text_path] = text_data_block(spec)
        out[spec.text_path + ".meta"] = asset_meta(spec.text_path)
        out[spec.logic_path] = logic_prefab(spec)
        out[spec.logic_path + ".meta"] = prefab_meta(spec.logic_path)
        out[spec.graphics_path] = graphics_prefab(spec)
        out[spec.graphics_path + ".meta"] = prefab_meta(spec.graphics_path)

    out["SpriteAssetManifest.asset"] = sprite_asset_manifest(SPECS)
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


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true",
                        help="report stale files and exit non-zero instead of writing")
    args = parser.parse_args()

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
