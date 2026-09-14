import pathlib

from PIL import Image

# Beside this script. It used to be an absolute path on the machine that first ran it, which meant
# the generator could not be re-run anywhere else -- including the Windows box that does the builds.
OUT = pathlib.Path(__file__).resolve().parent

T = (0, 0, 0, 0)
# TWO OUTLINE TONES, NOT ONE. A single near-black line around the whole silhouette made the pylon and
# the bench look pasted onto the floor next to the game's own objects ("it alone sticks out, like it
# was composited in", 2026-09-11). The game's sprites outline in a dark shade of their own material
# and let the light come from above: top and side edges are the softer tone, only the underside gets
# the darkest one (the SDK bench: mid red along its top edge, the deepest red only on its feet).
# OUTLINE is now that underside tone; OUTLINE_LIT goes everywhere else.
OUTLINE = (36, 31, 46, 255)
OUTLINE_LIT = (52, 48, 68, 255)
SHADOW = (24, 21, 32, 120)
STONE = [
    (45, 41, 62, 255),
    (72, 66, 88, 255),
    (108, 100, 96, 255),
    (150, 138, 108, 255),
    (196, 182, 143, 255),
]
GLOW_ON = {"rim": (168, 95, 20, 255), "mid": (240, 160, 42, 255), "core": (255, 211, 92, 255)}
GLOW_OFF = {"rim": (47, 42, 60, 255), "mid": (61, 56, 80, 255), "core": (75, 69, 92, 255)}


class C:
    def __init__(self, w, h):
        self.w, self.h = w, h
        self.r = [[None] * w for _ in range(h)]

    def put(self, x, y, n):
        if 0 <= x < self.w and 0 <= y < self.h:
            self.r[y][x] = n

    def cells(self, n):
        return [(x, y) for y in range(self.h) for x in range(self.w) if self.r[y][x] == n]

    def all_cells(self):
        return [(x, y) for y in range(self.h) for x in range(self.w) if self.r[y][x] is not None]


def rect(c, x0, y0, x1, y1, n):
    for y in range(y0, y1 + 1):
        for x in range(x0, x1 + 1):
            c.put(x, y, n)


def disc(c, cx, cy, rad, n):
    for y in range(c.h):
        for x in range(c.w):
            if (x - cx) ** 2 + (y - cy) ** 2 <= rad * rad:
                c.put(x, y, n)


def trapezoid(c, ytop, ybot, xtl, xtr, xbl, xbr, n):
    span = max(1, ybot - ytop)
    for y in range(ytop, ybot + 1):
        t = (y - ytop) / span
        lx = round(xtl + (xbl - xtl) * t)
        rx = round(xtr + (xbr - xtr) * t)
        for x in range(lx, rx + 1):
            c.put(x, y, n)


def diamond(c, cx, cy, rx, ry, n):
    for y in range(c.h):
        for x in range(c.w):
            if abs(x - cx) / rx + abs(y - cy) / ry <= 1.0:
                c.put(x, y, n)


def ring(c, cx, cy, ro, ri, n):
    for y in range(c.h):
        for x in range(c.w):
            d = (x - cx) ** 2 + (y - cy) ** 2
            if ri * ri < d <= ro * ro:
                c.put(x, y, n)


def bounds(cells):
    xs = [p[0] for p in cells]
    ys = [p[1] for p in cells]
    return min(xs), min(ys), max(xs), max(ys)


def build_image(c, regions, shifts, glow_on, shadow=True):
    out = [[T] * c.w for _ in range(c.h)]
    box = {n: bounds(c.cells(n)) for n in regions}

    for y in range(c.h):
        for x in range(c.w):
            n = c.r[y][x]
            if n is None or n.startswith("glow"):
                continue
            x0, y0, x1, y1 = box[n]
            hh = max(1, y1 - y0)
            ww = max(1, x1 - x0)
            d = (y - y0) / hh
            lvl = 4 if d < 0.13 else 3 if d < 0.38 else 2 if d < 0.72 else 1
            fx = (x - x0) / ww
            if fx > 0.78:
                lvl -= 1
            elif fx < 0.16:
                lvl += 1
            lvl = max(0, min(4, lvl + shifts.get(n, 0)))
            out[y][x] = STONE[lvl]

    for n in regions:
        x0, y0, x1, y1 = box[n]
        for x in range(x0, x1 + 1):
            for y in range(y0, y1 + 1):
                if c.r[y][x] == n and out[y][x] != T:
                    out[y][x] = STONE[4]
                    break

    prev = [row[:] for row in out]
    darker = {STONE[i]: STONE[max(0, i - 2)] for i in range(5)}
    for y in range(1, c.h):
        for x in range(c.w):
            n, a = c.r[y][x], c.r[y - 1][x]
            if n and a and n != a and prev[y][x] in darker:
                out[y][x] = darker[prev[y][x]]

    for y in range(c.h):
        for x in range(c.w):
            if c.r[y][x] is None:
                continue
            for dx, dy in ((0, 1), (1, 0), (-1, 0), (0, -1)):
                nx, ny = x + dx, y + dy
                if not (0 <= nx < c.w and 0 <= ny < c.h) or c.r[ny][nx] is None:
                    # Below is checked first so a corner that is both a side and an underside
                    # takes the dark tone: the shadow wins where the object meets the floor.
                    out[y][x] = OUTLINE if dy == 1 else OUTLINE_LIT
                    break

    pal = GLOW_ON if glow_on else GLOW_OFF
    groups = {}
    for y in range(c.h):
        for x in range(c.w):
            n = c.r[y][x]
            if n and n.startswith("glow"):
                groups.setdefault(n, []).append((x, y))
    for g in groups.values():
        gs = set(g)
        ys = [p[1] for p in g]
        ymin, ymax = min(ys), max(ys)
        small = len(g) <= 8
        for x, y in g:
            nb = sum(1 for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)) if (x + dx, y + dy) in gs)
            if small:
                out[y][x] = pal["core"] if nb >= 2 else pal["mid"]
            elif nb <= 2:
                out[y][x] = pal["rim"]
            elif y <= ymin + max(1, (ymax - ymin) // 3):
                out[y][x] = pal["core"]
            else:
                out[y][x] = pal["mid"]
    if glow_on:
        # The lit state has to read at a glance from across a base, and one pixel of bleed did not:
        # in game the pylon looked identical switched on and off. The light now travels out from the
        # gem in rings that fade, so the body itself warms up.
        #
        # ONLY RGB CHANGES, NEVER ALPHA — that is what keeps design.md §7's promise that the two
        # states have the same silhouette. It holds by construction here rather than by anyone
        # remembering.
        frontier = {p for g in groups.values() for p in g}
        reached = set(frontier)
        for red, green, blue in ((96, 62, -26), (64, 41, -18), (38, 24, -11), (18, 11, -5)):
            nxt = set()
            for x, y in frontier:
                for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                    nx, ny = x + dx, y + dy
                    if not (0 <= nx < c.w and 0 <= ny < c.h) or (nx, ny) in reached:
                        continue
                    n = c.r[ny][nx]
                    if not n or n.startswith("glow"):
                        continue
                    reached.add((nx, ny))
                    nxt.add((nx, ny))
                    # The silhouette edge stays dark — light spreading onto it would blur the shape
                    # against the background — but the glow still travels past it to cells beyond.
                    if out[ny][nx] in (OUTLINE, OUTLINE_LIT):
                        continue
                    b = out[ny][nx]
                    out[ny][nx] = (min(255, b[0] + red), min(255, b[1] + green),
                                   max(0, b[2] + blue), 255)
            frontier = nxt

    img = Image.new("RGBA", (c.w, c.h), T)
    # NO BAKED SHADOW ON A SMALL ICON. The game draws a dropped item's shadow itself (DroppedItem
    # switches its own `shadow` object on), so one painted into the picture would be doubled.
    if shadow:
        ac = c.all_cells()
        bx0, _, bx1, by1 = bounds(ac)
        cx, rx = (bx0 + bx1) / 2, (bx1 - bx0) / 2 + 1.5
        for y in range(by1 - 1, min(c.h, by1 + 3)):
            for x in range(c.w):
                if c.r[y][x] is None and ((x - cx) / rx) ** 2 + ((y - by1) / 2.4) ** 2 <= 1.0:
                    img.putpixel((x, y), SHADOW)
    for y in range(c.h):
        for x in range(c.w):
            if out[y][x] != T:
                img.putpixel((x, y), out[y][x])
    return img


# FILL THE CANVAS. The SDK's own 1x1 workbench uses all 16x18 of it (rows 0..17, cols 0..15), and
# that is what makes a one-tile object look like it occupies its tile. Ours left margins and read as
# small and half-sunk in game.
def pylon(c):
    rect(c, 0, 13, 15, 17, "base")
    trapezoid(c, 5, 13, 4, 11, 2, 13, "shaft")
    trapezoid(c, 0, 5, 5, 10, 3, 12, "tip")
    diamond(c, 7.5, 8.0, 2.2, 3.2, "glow_gem")


def lens(c):
    # The ring has to be thick enough to survive the outline pass, which turns every cell touching
    # empty space dark. A 1.7px rim left about half a pixel of visible frame and the lens read as a
    # bare blob, so the glass is small and the rim wide rather than the other way round.
    rect(c, 7, 10, 8, 14, "handle")
    rect(c, 6, 10, 9, 11, "grip")
    ring(c, 7.5, 5.5, 5.0, 2.4, "frame")
    disc(c, 7.5, 5.5, 2.4, "glow_lens")


# THE WORKBENCH IS NOT STONE, SO IT DOES NOT GO THROUGH THE STONE SHADER.
#
# build_image shades every region with the same five-step STONE ramp and puts a dark outline on
# every cell that touches empty space. That is right for the pylon -- one lump of carved rock -- and
# it made the bench a flat grey-beige box that a human, seeing it beside the game's own benches,
# called "it alone has a different texture, and the size doesn't fit 1x1 either" (2026-09-11). The
# size was already right; the grammar was wrong. The game draws a bench in three-quarter view: a
# light TOP plane, a darker FRONT, a base with legs, props standing above the top edge, and the
# outline only on the silhouette (Examples/WorkbenchExample/Workbench/MyNewWorkbench1_down.png is
# the reference, and every row of it follows that pattern).
#
# So the bench is a pixel map: one character per pixel, one colour per character. It is the same
# "change a constant or a coordinate and regenerate" promise design.md §13 makes, just at the grain
# a piece of furniture needs. Wood and iron match the benches it will stand next to; the gem in the
# front plate is the lit pylon's own gold, straight from GLOW_ON, so the two objects read as one set
# and a change to the pylon's glow carries over here without anyone remembering to copy it.
WOOD = {
    "L": (232, 184, 119, 255),   # lit top
    "l": (201, 138, 74, 255),    # top
    "M": (154, 90, 48, 255),     # lip
    "D": (107, 58, 36, 255),     # front
    "d": (74, 40, 26, 255),      # shadow
}
IRON = {
    "I": (58, 61, 74, 255),      # dark
    "i": (92, 96, 112, 255),     # mid
    "j": (138, 143, 158, 255),   # light
}
BENCH_PALETTE = {
    ".": T,
    "O": OUTLINE,                # underside only
    "o": (96, 50, 30, 255),      # wood outline: top edge and sides
    "k": OUTLINE_LIT,            # iron and stone outline: props and leg sides
    "K": STONE[1],               # legs: the pylon's own body colour
    "G": GLOW_ON["mid"],
    "g": GLOW_ON["core"],
    "r": GLOW_ON["rim"],
    "h": (255, 242, 205, 255),   # one highlight pixel on the gem
    **WOOD,
    **IRON,
}
BENCH_ROWS = [
    "..kjj......kKk..",   # 0   props above the top edge: hammer head (left), a small pylon (right)
    ".kjjjk....kKgKk.",   # 1
    "oooooooooooooooo",   # 2   top edge, in wood
    "oLLLLLLLLLLLLLLo",   # 3   top plane
    "oLlllllLlllllllo",   # 4
    "olllllllllllLllo",   # 5
    "oMMMMMMMMMMMMMMo",   # 6   lip
    "oDDIIIIIIIIIIDDo",   # 7   front, with an iron plate
    "oDDIiiiirriiiIDo",   # 8
    "oDDIiiirGGGriiDo",   # 9   the pylon's gem, set in the plate
    "oDDIiirGghGGrIDo",   # 10
    "oDDIiiirGGGriiDo",   # 11
    "oDDIiiiirriiiIDo",   # 12
    "oDdIIIIIIIIIIdDo",   # 13
    "dddddddddddddddd",   # 14  underside of the body: wood shadow, not black
    "kKKk........kKKk",   # 15  legs, open in the middle like the reference
    "kKKk........kKKk",   # 16
    "OOOO........OOOO",   # 17  feet: the one place the darkest tone belongs
]


def bench_image():
    img = Image.new("RGBA", (16, 18), T)
    for y, row in enumerate(BENCH_ROWS):
        if len(row) != 16:
            raise ValueError(f"bench row {y} is {len(row)} wide, not 16")
        for x, ch in enumerate(row):
            img.putpixel((x, y), BENCH_PALETTE[ch])
    return img


HILITE = (255, 242, 205, 255)
HIGHLIGHTS = {"lens": [(6, 4), (7, 4), (6, 5)]}

def remote(c):
    rect(c, 7, 0, 9, 1, "tip")
    rect(c, 8, 1, 9, 4, "ant")
    rect(c, 3, 4, 12, 14, "body")
    rect(c, 5, 6, 10, 8, "glow_screen")
    rect(c, 4, 10, 6, 11, "btn")
    rect(c, 9, 10, 11, 11, "btn")
    rect(c, 4, 13, 5, 13, "glow_led")


# 1 tile is 16px in world (SpriteObject.PixelsPerUnit is a hardcoded 16f), so a placeable's art is
# 16 wide or it draws wider than the tiles it occupies. Height 18 rather than 16 is what the SDK's
# own 1x1 workbench uses (Examples/WorkbenchExample/Workbench/MyNewWorkbench1_down.png): the extra
# two rows let the object stand up out of its tile, and it is the size genassets.py's sprite_offset
# was copied for. Lens and remote never stand in the world -- they are inventory icons only -- so
# they are a plain 16x16.
DESIGNS = {
    # base -1 rather than -2: at -2 the base reached STONE[0], darker than OUTLINE_LIT, and the
    # side outline would have read as a highlight.
    "pylon": (16, 18, pylon, ["base", "shaft", "tip"], {"base": -1, "tip": 1}),
    "remote": (16, 16, remote, ["tip", "ant", "body", "btn"],
               {"tip": 1, "ant": 0, "body": 0, "btn": 2}),
    "lens": (16, 16, lens, ["handle", "grip", "frame"], {"handle": -1, "grip": -2, "frame": 1}),
    # The workbench is a pixel map (BENCH_ROWS above) and is added after this loop.
}

made = {}
for name, (w, h, fn, regs, sh) in DESIGNS.items():
    states = ["off", "on"] if name == "pylon" else ["on"]
    for st in states:
        c = C(w, h)
        fn(c)
        im = build_image(c, regs, sh, st == "on")
        for hx, hy in HIGHLIGHTS.get(name, []):
            im.putpixel((hx, hy), HILITE)
        key = f"{name}_{st}" if name == "pylon" else name
        im.save(OUT / f"{key}.png")
        made[key] = im

made["workbench"] = bench_image()
made["workbench"].save(OUT / "workbench.png")


# ------------------------------------------------------------------------------ small icons (10x10)
#
# The game shows a SMALL icon wherever an item is not sitting in a slot: lying on the floor, carried
# in the hand, listed as a crafting material. Reusing the 16px icons there drew ours oversized beside
# the game's own (tester, 2026-09-14). The reference mods ship 10x10 at 16 pixels per unit
# (ConveyorTunnelIcon_inHand.png), and halving 16px art smears it, so these are drawn at 10.
#
# Same shapes, shading and palette as the big ones, fewer pixels. No baked shadow: the game draws a
# dropped item's shadow itself. The pylon is drawn switched off, because an item is never lit.
SMALL = 10


def pylon_small(c):
    rect(c, 0, 7, 9, 9, "base")
    trapezoid(c, 3, 7, 3, 6, 1, 8, "shaft")
    trapezoid(c, 0, 3, 3, 6, 2, 7, "tip")
    diamond(c, 4.5, 4.5, 1.5, 2.0, "glow_gem")


def lens_small(c):
    rect(c, 4, 7, 5, 9, "handle")
    rect(c, 3, 7, 6, 7, "grip")
    ring(c, 4.5, 3.5, 3.5, 1.8, "frame")
    disc(c, 4.5, 3.5, 1.8, "glow_lens")


def remote_small(c):
    rect(c, 5, 0, 6, 0, "tip")
    rect(c, 5, 1, 6, 2, "ant")
    rect(c, 1, 3, 8, 9, "body")
    rect(c, 3, 4, 6, 5, "glow_screen")
    rect(c, 2, 7, 3, 7, "btn")
    rect(c, 6, 7, 7, 7, "btn")
    rect(c, 2, 8, 2, 8, "glow_led")


# The bench's grammar at ten pixels: props above, wood top, lip, iron plate with the gem, shadowed
# underside, legs. Same palette as BENCH_ROWS, so the two read as one object.
BENCH_SMALL_ROWS = [
    "..kjk..kgk",   # 0  props: hammer head (left), the pylon's gem (right)
    "oooooooooo",   # 1  top edge, in wood
    "oLLLLLLLLo",   # 2  top plane
    "olllllLllo",   # 3
    "oMMMMMMMMo",   # 4  lip
    "oDIIIIIIDo",   # 5  front, with an iron plate
    "oDIrGgrIDo",   # 6  the gem, set in the plate
    "dddddddddd",   # 7  underside: wood shadow, not black
    "kKk....kKk",   # 8  legs
    "OOO....OOO",   # 9  feet
]


def bench_small_image():
    if len(BENCH_SMALL_ROWS) != SMALL:
        raise ValueError(f"small bench is {len(BENCH_SMALL_ROWS)} rows, not {SMALL}")
    img = Image.new("RGBA", (SMALL, SMALL), T)
    for y, row in enumerate(BENCH_SMALL_ROWS):
        if len(row) != SMALL:
            raise ValueError(f"small bench row {y} is {len(row)} wide, not {SMALL}")
        for x, ch in enumerate(row):
            img.putpixel((x, y), BENCH_PALETTE[ch])
    return img


SMALL_DESIGNS = {
    "pylon": (pylon_small, ["base", "shaft", "tip"], {"base": -1, "tip": 1}),
    "remote": (remote_small, ["tip", "ant", "body", "btn"],
               {"tip": 1, "ant": 0, "body": 0, "btn": 2}),
    "lens": (lens_small, ["handle", "grip", "frame"], {"handle": -1, "grip": -2, "frame": 1}),
}
SMALL_HIGHLIGHTS = {"lens": [(4, 2), (3, 3)]}

small = {}
for name, (fn, regs, sh) in SMALL_DESIGNS.items():
    c = C(SMALL, SMALL)
    fn(c)
    im = build_image(c, regs, sh, glow_on=name != "pylon", shadow=False)
    for hx, hy in SMALL_HIGHLIGHTS.get(name, []):
        im.putpixel((hx, hy), HILITE)
    small[name] = im
small["workbench"] = bench_small_image()
for name, im in small.items():
    im.save(OUT / f"{name}_small.png")

SC, PAD = 8, 20
BG = (30, 29, 27, 255)
order = ["pylon_off", "pylon_on", "lens", "remote", "workbench"]
# Each small icon sits under the big one it belongs to, at the same scale, so the sheet shows how
# much smaller they draw. The lit pylon has none: an item is never lit.
small_under = {"pylon_off": "pylon", "lens": "lens", "remote": "remote", "workbench": "workbench"}
w = PAD * (len(order) + 1) + sum(made[n].width for n in order) * SC
# Tallest of the set rather than a constant: the designs are no longer all the same height.
big_h = max(made[n].height for n in order) * SC
h = PAD * 3 + big_h + SMALL * SC
sheet = Image.new("RGBA", (w, h), BG)
ox = PAD
for n in order:
    im = made[n]
    sheet.alpha_composite(im.resize((im.width * SC, im.height * SC), Image.NEAREST), (ox, PAD))
    if n in small_under:
        s = small[small_under[n]]
        sheet.alpha_composite(s.resize((s.width * SC, s.height * SC), Image.NEAREST),
                              (ox, PAD * 2 + big_h))
    ox += im.width * SC + PAD
sheet.save(OUT / "preview.png")


# ------------------------------------------------------------------------------------ store image
#
# Both stores take .png or .jpg and check nothing else client-side; mod.io re-renders the logo at
# 1280x720, 640x360 and 320x180, so 16:9 at 1280x720 is the size that survives every crop.
#
# NO TEXT. Drawing the mod's name would need a font file, and either we depend on whatever TTF a
# machine happens to have -- which makes this script non-reproducible, the one property the whole
# generator is built around -- or we hand-draw glyphs, which is a lot of pixels for something both
# stores already print beside the image. So the picture says it instead: the lit pylon, and the
# square it protects drawn in the lens's own marker colour, with the other three below it.
SW, SH = 1280, 720
TILE = 40                      # one game tile, in store-image pixels
store = Image.new("RGBA", (SW, SH), STONE[0])

# A tile grid, so the square below reads as a measured area rather than a decoration.
grid = (56, 51, 74, 255)
for gx in range(0, SW, TILE):
    for gy in range(SH):
        store.putpixel((gx, gy), grid)
for gy in range(0, SH, TILE):
    for gx in range(SW):
        store.putpixel((gx, gy), grid)

# The protected square. design.md §7's outline is the lens's whole reason to exist, so the store
# image shows what the lens shows rather than inventing a look for it.
MARKER = (150, 214, 240, 255)
# Six tiles either side of the centre, which fills the height without touching it and leaves the
# tool row below clear of the border. At the 320x180 crop both stores also render, the square and
# the pylon inside it are still the only two things the eye has to resolve.
half = 6 * TILE
cx, cy = SW // 2, 310
for t in range(3):
    x0, y0, x1, y1 = cx - half + t, cy - half + t, cx + half - t, cy + half - t
    for x in range(x0, x1):
        store.putpixel((x, y0), MARKER)
        store.putpixel((x, y1), MARKER)
    for y in range(y0, y1):
        store.putpixel((x0, y), MARKER)
        store.putpixel((x1, y), MARKER)

def paste(img, scale, centre_x, bottom_y):
    big = img.resize((img.width * scale, img.height * scale), Image.NEAREST)
    store.alpha_composite(big, (centre_x - big.width // 2, bottom_y - big.height))

paste(made["pylon_on"], 17, cx, cy + half - TILE)
for i, name in enumerate(["workbench", "lens", "remote"]):
    paste(made[name], 5, cx + (i - 1) * 200, SH - 30)

store.convert("RGB").save(OUT / "store_thumbnail.png")
print("ok", sheet.size, store.size)
