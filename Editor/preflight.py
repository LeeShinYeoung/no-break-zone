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

REPO = pathlib.Path(__file__).resolve().parent.parent
FILEID_TABLE = REPO / "Editor" / "GameData" / "script_fileids.csv"

# The game's authoring assembly. Unity engine assemblies use other GUIDs and are copied verbatim
# from reference prefabs, so they are out of scope for check 1.
GAME_ASSEMBLY_GUID = "3392f4c23e1d8662d749dabb2361ee02"

# Rejected by the mod safety check when the game recompiles Scripts/ at load
# (official modding docs; research.md 6장). A hit here means the mod silently fails to load with
# "CompileFailed" in Player.log rather than anything pointing at the real cause.
BANNED_NAMESPACES = [
    "System.IO",
    "System.Diagnostics",
    "System.Net",
    "System.Runtime.InteropServices",
    "System.Reflection",
    "System.AppDomain",
]

# Everything under these needs a sibling .meta. Dot-directories are invisible to Unity (CLAUDE.md
# §1-3) and so is the meta file itself.
ASSET_ROOTS = ["Scripts", "Data", "Prefabs", "Textures", "Editor", "Localization", "Conf"]
META_EXEMPT_SUFFIXES = {".meta"}

SCRIPT_PATTERN = re.compile(r"m_Script:\s*\{fileID:\s*(-?\d+),\s*guid:\s*(\w+)")


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


def load_fileid_table():
    if not FILEID_TABLE.exists():
        return None
    with FILEID_TABLE.open(encoding="utf-8") as handle:
        return {int(row["fileID"]): row["fullName"] for row in csv.DictReader(handle)}


def tracked_asset_files():
    for root in ASSET_ROOTS:
        base = REPO / root
        if not base.is_dir():
            continue
        for path in base.rglob("*"):
            if any(part.startswith(".") for part in path.relative_to(REPO).parts):
                continue
            yield path


def check_prefab_scripts(problems):
    table = load_fileid_table()
    if table is None:
        problems.append(f"reference table missing: {FILEID_TABLE.relative_to(REPO)}")
        return 0

    checked = 0
    for path in REPO.rglob("*.prefab"):
        if any(part.startswith(".") for part in path.relative_to(REPO).parts):
            continue
        text = path.read_text(encoding="utf-8", errors="ignore")
        for file_id, guid in SCRIPT_PATTERN.findall(text):
            if guid != GAME_ASSEMBLY_GUID:
                continue
            checked += 1
            if int(file_id) not in table:
                problems.append(
                    f"{path.relative_to(REPO)}: m_Script fileID {file_id} matches no game class"
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
        "prefab m_Script refs": check_prefab_scripts(problems),
        "scripts scanned for banned namespaces": check_banned_namespaces(problems),
        "assets checked for .meta": check_meta_pairs(problems),
        "asmdef references": check_asmdef_references(problems),
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
