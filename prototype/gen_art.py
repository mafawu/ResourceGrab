"""Demo artwork generator (v2) for the Yingku-style home prototype.

Flat-vector landscapes built from silhouette envelopes rather than zig-zag
polygons, with a blurred radial light, atmospheric haze between depth layers
and a vignette. Rendered at 2x and downsampled for clean edges.

Output:
    Assets/poster_XX.png   600x900  (2:3)
    Assets/backdrop_XX.png 1600x900 (16:9)
"""
from __future__ import annotations

import math
import os
import random

from PIL import Image, ImageDraw, ImageFilter

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "YingkuHome", "Assets")
os.makedirs(OUT, exist_ok=True)

SS = 2  # supersampling


# ---------------------------------------------------------------- colour
def lerp(a, b, t):
    t = max(0.0, min(1.0, t))
    return tuple(int(round(a[i] + (b[i] - a[i]) * t)) for i in range(3))


def vgrad(w, h, top, bottom):
    strip = Image.new("RGB", (1, h))
    d = ImageDraw.Draw(strip)
    for y in range(h):
        d.point((0, y), lerp(top, bottom, y / max(1, h - 1)))
    return strip.resize((w, h), Image.BILINEAR)


def rgba(color, alpha):
    return (color[0], color[1], color[2], alpha)


# ---------------------------------------------------------------- shapes
def envelope(w, h, base, amp, peaks, rng):
    """Silhouette y(x): the highest of N triangle peaks."""
    ys = [float(base)] * (w + 1)
    for _ in range(peaks):
        cx = rng.uniform(-w * 0.1, w * 1.1)
        hw = rng.uniform(w * 0.16, w * 0.42)
        hh = amp * rng.uniform(0.55, 1.0)
        peak = base - hh
        x0, x1 = int(max(0, cx - hw)), int(min(w, cx + hw))
        for x in range(x0, x1 + 1):
            t = abs(x - cx) / hw
            # slightly convex slope reads more like a mountain than a straight flank
            y = peak + (base - peak) * (t ** 1.12)
            if y < ys[x]:
                ys[x] = y
    return ys


def draw_layer(img, ys, h, color, bottom=None, snow=None):
    w = img.width
    bottom = h if bottom is None else bottom
    poly = [(0, bottom)] + [(x, ys[x]) for x in range(0, w + 1, 2)] + [(w, bottom)]
    layer = Image.new("RGBA", img.size, (0, 0, 0, 0))
    ImageDraw.Draw(layer).polygon(poly, fill=rgba(color, 255))
    img.alpha_composite(layer)

    if snow:
        mask = Image.new("L", img.size, 0)
        ImageDraw.Draw(mask).polygon(poly, fill=255)
        band = Image.new("RGBA", img.size, (0, 0, 0, 0))
        bd = ImageDraw.Draw(band)
        # paint a horizontal band just under the ridge, clipped by the mask
        bd.rectangle([0, 0, w, h], fill=rgba(snow, 210))
        band.putalpha(Image.composite(band.getchannel("A"), Image.new("L", img.size, 0), mask))
        band = band.filter(ImageFilter.GaussianBlur(1.5))
        img.alpha_composite(band)


def light(img, cx, cy, r, color, strength=140):
    """Soft radial light: blurred disc instead of concentric rings."""
    size = int(r * 8)
    disc = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(disc)
    d.ellipse([size / 2 - r, size / 2 - r, size / 2 + r, size / 2 + r], fill=rgba(color, 255))
    disc = disc.filter(ImageFilter.GaussianBlur(r * 2.1))
    alpha = disc.getchannel("A").point(lambda v: int(v * strength / 255))
    disc.putalpha(alpha)
    img.alpha_composite(disc, (int(cx - size / 2), int(cy - size / 2)))


def haze(img, y0, y1, color, strength=70):
    """Soft atmospheric band: alpha ramps up and back down, no hard edges."""
    w, h = img.size
    y0, y1 = max(0, int(y0)), min(h, int(y1))
    if y1 <= y0:
        return
    ramp = Image.new("L", (1, y1 - y0))
    for y in range(y1 - y0):
        ramp.putpixel((0, y), int(strength * math.sin(math.pi * y / (y1 - y0))))
    band = Image.new("RGBA", (w, y1 - y0), rgba(color, 255))
    band.putalpha(ramp.resize((w, y1 - y0)))
    img.alpha_composite(band, (0, y0))


def vignette(img, strength=90):
    w, h = img.size
    m = Image.new("L", (w, h), 0)
    d = ImageDraw.Draw(m)
    d.ellipse([-w * 0.25, -h * 0.35, w * 1.25, h * 1.35], fill=255)
    m = m.filter(ImageFilter.GaussianBlur(min(w, h) * 0.12))
    dark = Image.new("RGBA", (w, h), (0, 0, 0, strength))
    dark.putalpha(m.point(lambda v: int((255 - v) * strength / 255)))
    img.alpha_composite(dark)


def trees(draw, w, base_y, scale, color, rng, count=16, spread=(0.0, 1.0)):
    for _ in range(count):
        x = rng.uniform(w * spread[0], w * spread[1])
        hgt = rng.uniform(0.45, 1.0) * scale
        wid = hgt * 0.40
        draw.polygon([(x, base_y - hgt), (x - wid / 2, base_y), (x + wid / 2, base_y)],
                     fill=rgba(color, 255))


def cityline(img, base, rng, far, near, window, layers=3):
    w, h = img.size
    for layer in range(layers):
        col = lerp(far, near, 0.35 + layer * 0.3)
        y0 = base + layer * h * 0.035
        x = -int(w * 0.02)
        while x < w:
            bw = rng.uniform(w * 0.025, w * 0.06)
            bh = rng.uniform(h * 0.08, h * 0.26) * (1 - layer * 0.10)
            d = ImageDraw.Draw(img)
            d.rectangle([x, y0 - bh, x + bw, y0], fill=rgba(col, 255))
            if layer == layers - 1:
                for gy in range(int(bh // (h * 0.028))):
                    for gx in range(2):
                        if rng.random() < 0.5:
                            wx = x + bw * (0.24 + gx * 0.36)
                            wy = y0 - bh + h * 0.012 + gy * h * 0.028
                            d.rectangle([wx, wy, wx + bw * 0.10, wy + h * 0.006],
                                        fill=rgba(window, 210))
            x += bw + rng.uniform(w * 0.004, w * 0.014)


# ---------------------------------------------------------------- scene
def scene(kind, w, h, pal, rng):
    img = Image.new("RGBA", (w, h), (0, 0, 0, 255))
    img.paste(vgrad(w, h, pal["sky_top"], pal["sky_bottom"]).convert("RGBA"), (0, 0))

    horizon = int(h * 0.63)

    if pal.get("stars"):
        d = ImageDraw.Draw(img)
        for _ in range(int(w * h / 9000)):
            x, y = rng.uniform(0, w), rng.uniform(0, horizon * 0.9)
            r = rng.uniform(0.6, 1.7) * (w / 800)
            d.ellipse([x - r, y - r, x + r, y + r],
                      fill=(255, 255, 255, int(rng.uniform(30, 130))))

    if pal.get("sun"):
        cx, cy = w * pal.get("sun_x", 0.7), h * pal.get("sun_y", 0.28)
        r = h * pal.get("sun_r", 0.062)
        light(img, cx, cy, r * 2.4, pal["sun"], 120)
        d = ImageDraw.Draw(img)
        d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=rgba(pal["sun"], 255))

    if kind == "city":
        haze(img, int(h * 0.42), horizon + int(h * 0.1), pal["sky_bottom"], 40)
        cityline(img, int(h * 0.70), rng, pal["far"], pal["near"], pal["window"])
        ground = ImageDraw.Draw(img)
        ground.rectangle([0, int(h * 0.80), w, h], fill=rgba(pal["near"], 255))
        # wet road reflection
        for i in range(6):
            y = h * (0.82 + i * 0.028)
            ground.rectangle([w * 0.42 - w * 0.02 * i, y, w * 0.58 + w * 0.02 * i, y + h * 0.004],
                             fill=rgba(pal["window"], 26))
        vignette(img, 100)
        return img

    if kind in ("desert", "sea"):
        ground_col = pal["water"]
        ImageDraw.Draw(img).rectangle([0, horizon, w, h], fill=rgba(ground_col, 255))

    # depth layers
    layers = pal.get("layers", 4)
    for i in range(layers):
        t = i / max(1, layers - 1)
        col = lerp(pal["far"], pal["near"], t)
        base = horizon - h * (0.22 - i * 0.075)
        amp = h * (0.22 - i * 0.03)
        ys = envelope(w, h, base, amp, pal.get("peaks", 4) + i, rng)
        snow = pal.get("snow") if (pal.get("snow") and i == 0) else None
        stop = horizon if kind in ("desert", "sea") else h
        draw_layer(img, ys, h, col, bottom=stop, snow=snow)
        if i == 0:
            haze(img, int(base - amp), int(base + h * 0.10), pal["sky_bottom"], 55)

    if kind == "forest":
        layers_near = ImageDraw.Draw(img)
        trees(layers_near, w, h * 0.99, h * 0.20, pal["near"], rng, 16)
        trees(layers_near, w, h * 1.03, h * 0.26, lerp(pal["near"], (0, 0, 0), 0.35), rng, 12)

    if kind == "sea":
        # soft light path on the water: a blurred column, no hard dashes
        if pal.get("sun"):
            sx = w * pal.get("sun_x", 0.7)
            col = Image.new("RGBA", img.size, (0, 0, 0, 0))
            cd = ImageDraw.Draw(col)
            cd.rectangle([sx - w * 0.016, horizon, sx + w * 0.016, h], fill=rgba(pal["sun"], 46))
            col = col.filter(ImageFilter.GaussianBlur(w * 0.018))
            img.alpha_composite(col)

    if kind == "desert":
        d = ImageDraw.Draw(img)
        for i in range(3):
            col = lerp(pal["far"], pal["near"], 0.55 + i * 0.2)
            y = horizon + h * (0.03 + i * 0.09)
            pts = [(0, h)]
            for x in range(0, w + 24, 24):
                pts.append((x, y + h * 0.035 * math.sin(x / w * 5.2 + i * 1.9)))
            pts.append((w, h))
            d.polygon(pts, fill=rgba(col, 255))

    if kind == "aurora":
        bands = Image.new("RGBA", img.size, (0, 0, 0, 0))
        bd = ImageDraw.Draw(bands)
        for b in range(6):
            col = pal["accent"] if b % 2 == 0 else pal["far"]
            bx = w * (0.02 + b * 0.17)
            top, bot = [], []
            for i in range(30):
                t = i / 29
                x = bx + math.sin(t * 4.4 + b * 0.9) * w * 0.06 + t * w * 0.10
                top.append((x, h * (0.12 + t * 0.26)))
                bot.append((x + w * 0.05, h * (0.16 + t * 0.26)))
            bd.polygon(top + bot[::-1], fill=rgba(col, 42))
        bands = bands.filter(ImageFilter.GaussianBlur(w * 0.012))
        img.alpha_composite(bands)

    vignette(img, 70)
    return img


# ---------------------------------------------------------------- palettes
PALETTES = [
    # 01 逐光之旅 — dawn over the bay, warm sky, teal water
    dict(kind="sea", sky_top=(38, 46, 74), sky_bottom=(196, 150, 118), sun=(252, 232, 186),
         far=(72, 96, 116), near=(20, 32, 44), water=(28, 52, 64), sun_x=0.70, sun_y=0.36,
         sun_r=0.055, peaks=4),
    # 02 星海远航 — moonlit sea
    dict(kind="sea", sky_top=(10, 20, 40), sky_bottom=(26, 54, 78), sun=(226, 236, 240),
         far=(30, 56, 78), near=(10, 20, 30), water=(12, 26, 38), stars=True,
         sun_x=0.66, sun_y=0.24, sun_r=0.045, peaks=3),
    # 03 灯塔来信 — dusk, lighthouse
    dict(kind="sea", sky_top=(46, 36, 46), sky_bottom=(226, 168, 118), sun=(255, 226, 168),
         far=(96, 74, 78), near=(30, 24, 30), water=(40, 34, 42), sun_x=0.62, sun_y=0.44,
         sun_r=0.07, peaks=2),
    # 04 森林之约 — green valley
    dict(kind="forest", sky_top=(22, 48, 42), sky_bottom=(140, 176, 132), sun=(226, 240, 208),
         far=(58, 92, 70), near=(14, 32, 26), water=(12, 26, 20), sun_x=0.30, sun_y=0.22,
         sun_r=0.05, peaks=4),
    # 05 山间列车 — pale alpine
    dict(kind="mountain", sky_top=(110, 142, 164), sky_bottom=(216, 220, 214), sun=(250, 246, 228),
         far=(140, 162, 174), near=(40, 56, 66), water=(150, 164, 170), snow=(240, 246, 248),
         sun_x=0.34, sun_y=0.26, sun_r=0.048, peaks=4),
    # 06 雨后之城 — purple city
    dict(kind="city", sky_top=(28, 24, 52), sky_bottom=(112, 78, 108), sun=(248, 170, 128),
         far=(70, 54, 92), near=(14, 14, 24), water=(12, 12, 20), window=(252, 198, 128),
         sun_x=0.52, sun_y=0.50, sun_r=0.055),
    # 07 雪山回响 — icy
    dict(kind="mountain", sky_top=(30, 56, 92), sky_bottom=(150, 184, 204), sun=(250, 246, 224),
         far=(104, 140, 168), near=(24, 40, 58), water=(96, 128, 154), snow=(242, 246, 250),
         sun_x=0.76, sun_y=0.24, sun_r=0.05, peaks=5),
    # 08 夏日乐章 — meadow
    dict(kind="forest", sky_top=(74, 104, 88), sky_bottom=(196, 208, 158), sun=(252, 240, 190),
         far=(128, 152, 108), near=(40, 64, 44), water=(60, 88, 60), sun_x=0.38, sun_y=0.20,
         sun_r=0.052, peaks=3),
    # 09 沙海行记 — dunes
    dict(kind="desert", sky_top=(104, 62, 44), sky_bottom=(232, 178, 118), sun=(252, 226, 172),
         far=(190, 134, 84), near=(120, 76, 48), water=(156, 104, 66), sun_x=0.68, sun_y=0.26,
         sun_r=0.055, peaks=3),
    # 10 蔚蓝世界 — deep teal
    dict(kind="sea", sky_top=(8, 30, 44), sky_bottom=(20, 86, 96), sun=(176, 240, 232),
         far=(20, 88, 96), near=(6, 28, 38), water=(8, 48, 58), sun_x=0.50, sun_y=0.30,
         sun_r=0.05, peaks=3),
    # 11 星河彼岸 — night mountains
    dict(kind="mountain", sky_top=(12, 16, 38), sky_bottom=(44, 48, 88), sun=(214, 220, 250),
         far=(44, 48, 90), near=(10, 12, 26), water=(12, 14, 28), stars=True,
         sun_x=0.28, sun_y=0.20, sun_r=0.038, peaks=4),
    # 12 极光之境 — aurora
    dict(kind="aurora", sky_top=(8, 28, 34), sky_bottom=(26, 70, 76), sun=(196, 250, 236),
         far=(28, 70, 74), near=(8, 28, 32), water=(10, 34, 38), accent=(120, 230, 210),
         stars=True, sun_x=0.74, sun_y=0.26, sun_r=0.04, peaks=3),
]

ITEMS = [
    ("DEMO-001", "逐光之旅", "CHASING THE FIRST LIGHT"),
    ("DEMO-002", "星海远航", "BEYOND THE TIDE"),
    ("DEMO-003", "灯塔来信", "LETTERS FROM THE LIGHT"),
    ("DEMO-004", "森林之约", "WHERE THE FOREST WAITS"),
    ("DEMO-005", "山间列车", "THE MOUNTAIN LINE"),
    ("DEMO-006", "雨后之城", "AFTER THE RAIN"),
    ("DEMO-007", "雪山回响", "ECHOES OF SNOW"),
    ("DEMO-008", "夏日乐章", "A SUMMER MELODY"),
    ("DEMO-009", "沙海行记", "ACROSS THE DUNES"),
    ("DEMO-010", "蔚蓝世界", "INTO THE BLUE"),
    ("DEMO-011", "星河彼岸", "THE OTHER SHORE"),
    ("DEMO-012", "极光之境", "UNDER THE AURORA"),
]


def brighten(pal, t=0.16):
    out = dict(pal)
    for k in ("sky_top", "sky_bottom", "far", "near", "water", "sun", "snow", "window", "accent"):
        if isinstance(pal.get(k), tuple):
            out[k] = lerp(pal[k], (255, 255, 255), t)
    return out


def render(kind, w, h, pal, seed):
    rng = random.Random(seed)
    big = scene(kind, w * SS, h * SS, pal, rng).convert("RGB")
    return big.resize((w, h), Image.LANCZOS)


def main():
    for i, (code, title, en) in enumerate(ITEMS, start=1):
        pal = PALETTES[i - 1]
        render(pal["kind"], 600, 900, pal, seed=i * 131).save(
            os.path.join(OUT, f"poster_{i:02d}.png"), optimize=True)
        render(pal["kind"], 1600, 520, pal, seed=i * 131 + 7).save(
            os.path.join(OUT, f"backdrop_{i:02d}.png"), optimize=True)
        render(pal["kind"], 1200, 820, brighten(pal), seed=i * 131 + 53).save(
            os.path.join(OUT, f"still_{i:02d}.png"), optimize=True)
    print("generated", len(ITEMS), "posters + backdrops + stills ->", OUT)


if __name__ == "__main__":
    main()



