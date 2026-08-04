from PIL import Image

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
        allglow = {p for g in groups.values() for p in g}
        for x, y in allglow:
            for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                nx, ny = x + dx, y + dy
                if 0 <= nx < c.w and 0 <= ny < c.h:
                    n = c.r[ny][nx]
                    if n and not n.startswith("glow") and out[ny][nx] != OUTLINE:
                        b = out[ny][nx]
                        out[ny][nx] = (min(255, b[0] + 60), min(255, b[1] + 38), max(0, b[2] - 18), 255)

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


def pylon(c):
    rect(c, 5, 26, 26, 29, "base")
    trapezoid(c, 8, 26, 12, 19, 10, 21, "shaft")
    trapezoid(c, 3, 8, 15, 16, 12, 19, "tip")
    diamond(c, 15.5, 16.5, 2.6, 4.0, "glow_gem")


def lens(c):
    rect(c, 14, 19, 17, 30, "handle")
    rect(c, 12, 23, 19, 26, "grip")
    ring(c, 15.5, 12.0, 9.2, 6.2, "frame")
    disc(c, 15.5, 12.0, 6.2, "glow_lens")


def bench(c):
    rect(c, 7, 24, 15, 30, "legs")
    rect(c, 48, 24, 56, 30, "legs")
    rect(c, 2, 18, 61, 24, "top")
    rect(c, 15, 8, 48, 19, "back")
    rect(c, 11, 5, 52, 9, "hood")
    rect(c, 18, 11, 25, 17, "panel")
    rect(c, 38, 11, 45, 17, "panel")
    disc(c, 31.5, 13.5, 4.6, "glow_socket")
    rect(c, 20, 13, 23, 14, "glow_l")
    rect(c, 40, 13, 43, 14, "glow_r")


HILITE = (255, 242, 205, 255)
HIGHLIGHTS = {"lens": [(12, 8), (13, 8), (11, 9), (12, 9), (11, 10)]}

def remote(c):
    rect(c, 16, 1, 20, 3, "tip")
    rect(c, 17, 3, 19, 9, "ant")
    rect(c, 8, 8, 24, 29, "body")
    rect(c, 10, 11, 22, 17, "glow_screen")
    rect(c, 10, 21, 15, 24, "btn")
    rect(c, 17, 21, 22, 24, "btn")
    rect(c, 10, 26, 12, 27, "glow_led")


DESIGNS = {
    "pylon": (32, 32, pylon, ["base", "shaft", "tip"], {"base": -2, "tip": 1}),
    "remote": (32, 32, remote, ["tip", "ant", "body", "btn"],
               {"tip": 1, "ant": 0, "body": 0, "btn": 2}),
    "lens": (32, 32, lens, ["handle", "grip", "frame"], {"handle": -1, "grip": -2, "frame": 1}),
    "workbench": (64, 32, bench, ["legs", "top", "back", "hood", "panel"],
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
        im.save(f"/home/claude/out/{key}.png")
        made[key] = im

SC, PAD = 8, 20
BG = (30, 29, 27, 255)
order = ["pylon_off", "pylon_on", "lens", "remote", "workbench"]
w = PAD * (len(order) + 1) + sum(made[n].width for n in order) * SC
h = PAD * 2 + 32 * SC
sheet = Image.new("RGBA", (w, h), BG)
ox = PAD
for n in order:
    im = made[n]
    sheet.alpha_composite(im.resize((im.width * SC, im.height * SC), Image.NEAREST), (ox, PAD))
    ox += im.width * SC + PAD
sheet.save("/home/claude/out/preview.png")
print("ok", sheet.size)
