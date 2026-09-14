#!/usr/bin/env python3
"""Static checks that can run on a machine without Unity.

Builds are Windows-only (CLAUDE.md §4), so code written on the Mac mirror goes unverified until
somebody runs Editor/build.ps1. These four checks close the gap for the failure modes that do not
need a compiler, and they are the only automated feedback available on the Mac.

    python3 Editor/preflight.py

This is NOT a compiler. Type errors, wrong method signatures and bad overloads are invisible here
and will only surface in the Windows build. What it does catch:

  1. prefab m_Script references that name a class the game does not have
  2. namespaces the game's mod safety check rejects at load time
  3. missing .meta files (Unity would reissue GUIDs and break every reference)
  4. asmdef references to assemblies that do not exist in this project
  5. m_address values that disagree with the asset's own guid

Check 1 works because Unity derives a MonoBehaviour's fileID deterministically from its class name:
    fileID = int32_le(MD4(b"s\\0\\0\\0" + namespace + classname)[:4])
That was verified against 184 real references across the SDK examples and a reference mod — all 184
resolved. Editor/GameData/script_fileids.csv holds the precomputed table; see that folder's README
for how to regenerate it after a game update.
"""

import csv
import json
import pathlib
import re
import struct
import sys
import uuid

REPO = pathlib.Path(__file__).resolve().parent.parent
FILEID_TABLE = REPO / "Editor" / "GameData" / "script_fileids.csv"

# What Unity itself reports for every MonoScript in the project, dumped by Editor/DumpScriptGuids.cs.
# Check 1 measures references against this rather than against values copied from other people's
# prefabs, which is how a whole set of dangling references shipped unnoticed.
SCRIPT_GUIDS = REPO / "Editor" / "GameData" / "script_guids.csv"

# Rejected by the mod safety check when the game recompiles Scripts/ at load
# (official modding docs; research.md chapter 6). A hit here means the mod silently fails to load with
# "CompileFailed" in Player.log rather than anything pointing at the real cause.
BANNED_NAMESPACES = [
    "System.IO",
    "System.Diagnostics",
    "System.Net",
    "System.Runtime.InteropServices",
    "System.Reflection",
    "System.AppDomain",
]

# Reflection reached through a method call names no namespace, so the list above cannot see it.
# obj.GetType().Name compiles to System.Reflection.MemberInfo::get_Name and the safety check
# rejected the whole assembly for it -- the mod did not load at all, over one log line.
BANNED_CALLS = [
    (".GetType()", "System.Reflection through GetType()"),
    (".GetMethod(", "System.Reflection member lookup"),
    (".GetProperty(", "System.Reflection member lookup"),
    (".GetField(", "System.Reflection member lookup"),
    (".GetMembers(", "System.Reflection member lookup"),
    (".InvokeMember(", "System.Reflection invocation"),
]

# Everything under these needs a sibling .meta. Dot-directories are invisible to Unity (CLAUDE.md
# §1-3) and so is the meta file itself.
ASSET_ROOTS = ["Scripts", "Data", "Prefabs", "Textures", "Editor", "Localization", "Conf"]

# Unity ignores any folder whose name ends in "~" and never writes a .meta inside one. That is the
# whole reason the offline logic runner lives at Editor/LogicTests~. Running it leaves bin/ and obj/
# behind, and this check used to report every one of those files as a missing .meta -- half a dozen
# failures that meant nothing and made a clean run impossible to recognise.
UNITY_IGNORED_SUFFIX = "~"
META_EXEMPT_SUFFIXES = {".meta"}

SCRIPT_PATTERN = re.compile(r"m_Script:\s*\{fileID:\s*(-?\d+),\s*guid:\s*(\w+)")

# An asset's own m_address sits at two-space indent; the same key nested deeper belongs to something
# else (a gradient map, a language entry) and is somebody else's id.
OWN_ADDRESS_PATTERN = re.compile(
    r"^  m_address:\n    m_low: (-?\d+)\n    m_high: (-?\d+)", re.MULTILINE
)
META_GUID_PATTERN = re.compile(r"^guid: (\w+)", re.MULTILINE)


def md4(msg: bytes) -> bytes:
    """Unity hashes script names with MD4, which hashlib no longer ships on most systems."""

    def rol(x, n):
        x &= 0xFFFFFFFF
        return ((x << n) | (x >> (32 - n))) & 0xFFFFFFFF

    h = [0x67452301, 0xEFCDAB89, 0x98BADCFE, 0x10325476]
    bit_len = len(msg) * 8
    msg += b"\x80" + b"\x00" * ((56 - (len(msg) + 1) % 64) % 64) + struct.pack("<Q", bit_len)

    for off in range(0, len(msg), 64):
        x = list(struct.unpack("<16I", msg[off:off + 64]))
        a, b, c, d = h
        for i, s in zip(range(16), [3, 7, 11, 19] * 4):
            f = (b & c) | (~b & d)
            a, b, c, d = d, rol(a + f + x[i], s), b, c
        for i, s in zip(range(16), [3, 5, 9, 13] * 4):
            g = (b & c) | (b & d) | (c & d)
            a, b, c, d = d, rol(a + g + x[(i % 4) * 4 + i // 4] + 0x5A827999, s), b, c
        order = [0, 8, 4, 12, 2, 10, 6, 14, 1, 9, 5, 13, 3, 11, 7, 15]
        for i, s in zip(range(16), [3, 9, 11, 15] * 4):
            e = b ^ c ^ d
            a, b, c, d = d, rol(a + e + x[order[i]] + 0x6ED9EBA1, s), b, c
        h = [(v + n) & 0xFFFFFFFF for v, n in zip(h, [a, b, c, d])]

    return struct.pack("<4I", *h)


def script_file_id(class_name: str, namespace: str = "") -> int:
    """fileID Unity will look for when a prefab references this class. Use when authoring prefabs."""
    return struct.unpack("<i", md4(b"s\x00\x00\x00" + (namespace + class_name).encode("utf-8"))[:4])[0]


def load_script_guids():
    if not SCRIPT_GUIDS.exists():
        return None
    with SCRIPT_GUIDS.open(newline="", encoding="utf-8") as handle:
        return {(int(row["fileID"]), row["guid"]): row["fullName"] for row in csv.DictReader(handle)}


def tracked_asset_files():
    for root in ASSET_ROOTS:
        base = REPO / root
        if not base.is_dir():
            continue
        for path in base.rglob("*"):
            parts = path.relative_to(REPO).parts
            if any(part.startswith(".") for part in parts):
                continue
            if any(part.endswith(UNITY_IGNORED_SUFFIX) for part in parts):
                continue
            yield path


def check_script_refs(problems):
    """Every m_Script must name a script Unity can actually find.

    This is the check that was missing when the generator shipped assembly guids copied from
    reference mods. Those guids named assemblies absent from this SDK install, so Unity wrote
    dangling references into the bundle — the build reported success and the game loaded none
    of the mod's items. A reference is only sound if the whole (fileID, guid) pair is one Unity
    emits, which is what script_guids.csv records.
    """
    table = load_script_guids()
    if table is None:
        problems.append(
            f"reference table missing: {SCRIPT_GUIDS.relative_to(REPO)} — "
            "run Editor/DumpScriptGuids.cs on Windows"
        )
        return 0

    checked = 0
    for pattern in ("*.prefab", "*.asset"):
        for path in sorted(REPO.rglob(pattern)):
            if any(part.startswith(".") for part in path.relative_to(REPO).parts):
                continue
            text = path.read_text(encoding="utf-8", errors="ignore")
            for file_id, guid in SCRIPT_PATTERN.findall(text):
                checked += 1
                if (int(file_id), guid) not in table:
                    problems.append(
                        f"{path.relative_to(REPO)}: m_Script {{fileID: {file_id}, guid: {guid}}} "
                        "resolves to no script in the project"
                    )
    return checked


def check_banned_namespaces(problems):
    checked = 0
    for path in (REPO / "Scripts").rglob("*.cs"):
        checked += 1
        for line_no, line in enumerate(path.read_text(encoding="utf-8", errors="ignore").splitlines(), 1):
            code = line.split("//", 1)[0]
            for banned in BANNED_NAMESPACES:
                if banned in code:
                    problems.append(
                        f"{path.relative_to(REPO)}:{line_no}: {banned} is rejected by the mod safety check"
                    )
            for call, why in BANNED_CALLS:
                if call in code:
                    problems.append(
                        f"{path.relative_to(REPO)}:{line_no}: '{call}' is {why}, "
                        "rejected by the mod safety check"
                    )
    return checked


def check_meta_pairs(problems):
    checked = 0
    for path in tracked_asset_files():
        if path.suffix in META_EXEMPT_SUFFIXES:
            continue
        checked += 1
        meta = path.with_name(path.name + ".meta")
        if not meta.exists():
            problems.append(f"{path.relative_to(REPO)}: missing {meta.name}")

    # A .meta with nothing beside it is just as broken — usually a delete that forgot the pair.
    for meta in REPO.rglob("*.meta"):
        if any(part.startswith(".") for part in meta.relative_to(REPO).parts):
            continue
        if not meta.with_suffix("").exists() and not pathlib.Path(str(meta)[:-5]).exists():
            problems.append(f"{meta.relative_to(REPO)}: orphaned, no matching asset")
    return checked


def check_data_block_addresses(problems):
    """A ScriptableObject's m_address must equal its own file guid.

    Core Keeper links sprites and text by a 128-bit address rather than by guid, so a wrong value
    fails silently: the object loads, the sprite does not, and nothing says why. The address is that
    asset's guid as a Microsoft-layout GUID (first three fields little-endian) split into two signed
    longs — reversing the SDK examples through this layout reproduces their .meta guids, 18 of 18.

    The game only demands uniqueness (reference mods disagree with their own guids and still work),
    so this checks the SDK convention rather than a hard requirement. It is worth checking anyway:
    it is the one cross-file identity a generator bug or a hand-edit could silently desync.
    """
    checked = 0
    for path in REPO.rglob("*.asset"):
        if any(part.startswith(".") for part in path.relative_to(REPO).parts):
            continue

        meta = pathlib.Path(str(path) + ".meta")
        if not meta.exists():
            continue  # the .meta check already reports this

        found = OWN_ADDRESS_PATTERN.search(path.read_text(encoding="utf-8", errors="ignore"))
        guid = META_GUID_PATTERN.search(meta.read_text(encoding="utf-8", errors="ignore"))
        if not found or not guid:
            continue  # not every .asset carries an address (the mod definition does not)

        checked += 1
        low, high = int(found.group(1)), int(found.group(2))
        derived = uuid.UUID(bytes_le=struct.pack("<qq", low, high)).hex
        if derived != guid.group(1):
            problems.append(
                f"{path.relative_to(REPO)}: m_address resolves to {derived}, "
                f"but the asset's guid is {guid.group(1)}"
            )
    return checked


def check_asmdef_references(problems):
    defined = set()
    asmdefs = []
    for path in REPO.rglob("*.asmdef"):
        if any(part.startswith(".") for part in path.relative_to(REPO).parts):
            continue
        data = json.loads(path.read_text(encoding="utf-8-sig"))
        defined.add(data["name"])
        asmdefs.append((path, data))

    # Assemblies that live outside this repo (Unity packages, the SDK, the game's own DLLs). We can
    # only confirm the ones defined here, so treat these as known-good rather than flagging them.
    external_prefixes = ("Unity.", "UnityEngine.", "UnityEditor.", "PugMod", "ModSDK", "Pug")

    checked = 0
    for path, data in asmdefs:
        for ref in data.get("references", []):
            checked += 1
            if ref in defined or ref.startswith(external_prefixes) or ref.startswith("GUID:"):
                continue
            problems.append(f"{path.relative_to(REPO)}: references unknown assembly '{ref}'")
    return checked


def main():
    problems = []
    counts = {
        "m_Script refs resolved": check_script_refs(problems),
        "scripts scanned for banned namespaces": check_banned_namespaces(problems),
        "assets checked for .meta": check_meta_pairs(problems),
        "asmdef references": check_asmdef_references(problems),
        "data block addresses": check_data_block_addresses(problems),
    }

    for label, count in counts.items():
        print(f"  {count:5}  {label}")

    if problems:
        print(f"\nFAIL — {len(problems)} problem(s):")
        for problem in problems:
            print(f"  - {problem}")
        return 1

    print("\nOK — static checks passed (this is not a compile; build on Windows to be sure)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
