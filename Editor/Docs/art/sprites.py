import pathlib

from PIL import Image

# Beside this script. It used to be an absolute path on the machine that first ran it, which meant
# the generator could not be re-run anywhere else -- including the Windows box that does the builds.
OUT = pathlib.Path(__file__).resolve().parent

T = (0, 0, 0, 0)
OUTLINE = (36, 31, 46, 255)
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


def build_image(c, regions, shifts, glow_on):
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
            for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                nx, ny = x + dx, y + dy
                if not (0 <= nx < c.w and 0 <= ny < c.h) or c.r[ny][nx] is None:
                    out[y][x] = OUTLINE
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
        # ONLY RGB CHANGES, NEVER ALPHA — that is what keeps 기획서 §7's promise that the two states
        # have the same silhouette. It holds by construction here rather than by anyone remembering.
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
                    if out[ny][nx] == OUTLINE:
                        continue
                    b = out[ny][nx]
                    out[ny][nx] = (min(255, b[0] + red), min(255, b[1] + green),
                                   max(0, b[2] + blue), 255)
            frontier = nxt

    img = Image.new("RGBA", (c.w, c.h), T)
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


def bench(c):
    rect(c, 1, 12, 4, 17, "legs")
    rect(c, 11, 12, 14, 17, "legs")
    rect(c, 1, 3, 14, 9, "back")
    rect(c, 0, 0, 15, 4, "hood")
    rect(c, 3, 5, 12, 8, "panel")
    rect(c, 0, 9, 15, 13, "top")
    disc(c, 7.5, 6.5, 1.9, "glow_socket")


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
    "pylon": (16, 18, pylon, ["base", "shaft", "tip"], {"base": -2, "tip": 1}),
    "remote": (16, 16, remote, ["tip", "ant", "body", "btn"],
               {"tip": 1, "ant": 0, "body": 0, "btn": 2}),
    "lens": (16, 16, lens, ["handle", "grip", "frame"], {"handle": -1, "grip": -2, "frame": 1}),
    "workbench": (16, 18, bench, ["legs", "top", "back", "hood", "panel"],
                  {"legs": -2, "top": 2, "back": 0, "hood": 1, "panel": -2}),
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

SC, PAD = 8, 20
BG = (30, 29, 27, 255)
order = ["pylon_off", "pylon_on", "lens", "remote", "workbench"]
w = PAD * (len(order) + 1) + sum(made[n].width for n in order) * SC
# Tallest of the set rather than a constant: the designs are no longer all the same height.
h = PAD * 2 + max(made[n].height for n in order) * SC
sheet = Image.new("RGBA", (w, h), BG)
ox = PAD
for n in order:
    im = made[n]
    sheet.alpha_composite(im.resize((im.width * SC, im.height * SC), Image.NEAREST), (ox, PAD))
    ox += im.width * SC + PAD
sheet.save(OUT / "preview.png")
print("ok", sheet.size)
