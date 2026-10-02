#!/usr/bin/env python3
"""LoupixDeck Audio Plugin Icon, Variante 3a (Drehregler, matt, nachtblau).

Benötigt: pip install pillow numpy
Aufruf:   python make_icon_3a.py [ausgabeordner]
Erzeugt icon_3a_{256,128,64,32,16}.png (RGBA, transparente Ecken).
"""
import math
import os
import sys

import numpy as np
from PIL import Image, ImageDraw, ImageFilter

SIZE = 256   # Designgröße (px)
SS = 4       # Supersampling
N = SIZE * SS
YY, XX = np.mgrid[0:N, 0:N].astype(np.float32)
XX = (XX + 0.5) / SS
YY = (YY + 0.5) / SS


def oklch(L, C, h, a=1.0):
    hr = math.radians(h)
    A, B = C * math.cos(hr), C * math.sin(hr)
    l = (L + 0.3963377774 * A + 0.2158037573 * B) ** 3
    m = (L - 0.1055613458 * A - 0.0638541728 * B) ** 3
    s = (L - 0.0894841775 * A - 1.2914855480 * B) ** 3
    lin = [4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
           -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
           -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s]
    out = [12.92 * c if c <= 0.0031308 else 1.055 * max(c, 0) ** (1 / 2.4) - 0.055 for c in lin]
    return (*[min(max(c, 0.0), 1.0) for c in out], a)


# Farben (identisch zu 3a)
BG = oklch(0.24, 0.02, 260)
EDGE = oklch(0.32, 0.02, 260)
ARC = oklch(0.80, 0.13, 200)
TRACK = oklch(0.34, 0.02, 260)
LINE = oklch(0.64, 0.21, 25)

# ---------- Masken ----------
def _mask(draw_fn):
    im = Image.new("L", (N, N), 0)
    draw_fn(ImageDraw.Draw(im))
    return np.asarray(im, dtype=np.float32) / 255.0


def circle(cx, cy, r):
    return _mask(lambda d: d.ellipse([(cx - r) * SS, (cy - r) * SS, (cx + r) * SS - 1, (cy + r) * SS - 1], fill=255))


def rrect(x, y, w, h, r):
    return _mask(lambda d: d.rounded_rectangle([x * SS, y * SS, (x + w) * SS - 1, (y + h) * SS - 1], radius=r * SS, fill=255))


def blur(mask, px):
    if px <= 0:
        return mask
    im = Image.fromarray((np.clip(mask, 0, 1) * 255).astype(np.uint8))
    im = im.filter(ImageFilter.GaussianBlur(px / 2 * SS))  # CSS blur = 2*sigma
    return np.asarray(im, dtype=np.float32) / 255.0


def shift(mask, dx, dy, fill=0.0):
    out = np.full_like(mask, fill)
    sx, sy = int(round(dx * SS)), int(round(dy * SS))
    h, w = mask.shape
    out[max(sy, 0):h + min(sy, 0), max(sx, 0):w + min(sx, 0)] = mask[max(-sy, 0):h + min(-sy, 0), max(-sx, 0):w + min(-sx, 0)]
    return out


# ---------- Compositing ----------
canvas = np.zeros((N, N, 4), dtype=np.float32)  # straight RGBA


def paint(color, alpha):
    """color: RGBA-Tupel oder HxWx3-Array; alpha: HxW-Maske (wird mit Farb-Alpha multipliziert)."""
    global canvas
    if isinstance(color, tuple):
        rgb = np.array(color[:3], dtype=np.float32)[None, None, :]
        a = alpha * color[3]
    else:
        rgb, a = color, alpha
    a = a[..., None]
    ca = canvas[..., 3:4]
    oa = a + ca * (1 - a)
    orgb = (rgb * a + canvas[..., :3] * ca * (1 - a)) / np.maximum(oa, 1e-6)
    canvas = np.concatenate([orgb, oa], axis=-1)


def drop_shadow(shape, dx, dy, blur_px, color, clip):
    paint(color, blur(shift(shape, dx, dy), blur_px) * clip)


def inset_shadow(shape, dx, dy, blur_px, color):
    paint(color, blur(shift(1 - shape, dx, dy, fill=1.0), blur_px) * shape)


def linear_gradient(box, css_deg, stops):
    x, y, w, h = box
    th = math.radians(css_deg)
    dx, dy = math.sin(th), -math.cos(th)
    L = abs(w * dx) + abs(h * dy)
    t = ((XX - (x + w / 2)) * dx + (YY - (y + h / 2)) * dy) / L + 0.5
    t = np.clip(t, 0, 1)
    pos = [s[0] for s in stops]
    return np.stack([np.interp(t, pos, [s[1][i] for s in stops]) for i in range(3)], axis=-1).astype(np.float32)


# ---------- Zeichnen ----------
icon = rrect(0, 0, SIZE, SIZE, 58)

# Hintergrund + 1px Innenkante
paint(BG, icon)
paint(EDGE, icon - rrect(1, 1, SIZE - 2, SIZE - 2, 57))

# Pegelring: Conic-Gradient ab 225°, Bogen 190°, Track bis 270°, Rest leer
ang = (np.degrees(np.arctan2(XX - 128, -(YY - 128))) % 360 - 225) % 360
ring = circle(128, 128, 92)
paint(ARC, ring * (ang < 190))
paint(TRACK, ring * ((ang >= 190) & (ang < 270)))
paint(BG, circle(128, 128, 74))  # Ring ausstanzen

# Regler-Körper (Kunststoff, matt)
KX, KY, KD = 66, 66, 124
knob = circle(KX + KD / 2, KY + KD / 2, KD / 2)
drop_shadow(knob, 0, 18, 26, oklch(0.04, 0.04, 260, 0.85), icon)
drop_shadow(knob, 0, 5, 3, oklch(0.06, 0.03, 260, 0.55), icon)
paint(linear_gradient((KX, KY, KD, KD), 160, [(0, oklch(0.93, 0.006, 260)[:3]), (1, oklch(0.78, 0.01, 260)[:3])]), knob)
inset_shadow(knob, 0, -3, 5, oklch(0.4, 0.02, 260, 0.30))
paint((1, 1, 1, 0.30), knob - circle(128, 128, KD / 2 - 1))

# Kappe
CX, CY, CD = KX + 9, KY + 9, KD - 18
cap = circle(CX + CD / 2, CY + CD / 2, CD / 2)
drop_shadow(cap, 0, 2, 4, oklch(0.2, 0.02, 260, 0.30), knob)
paint(linear_gradient((CX, CY, CD, CD), 165, [(0, oklch(0.90, 0.006, 260)[:3]), (1, oklch(0.80, 0.008, 260)[:3])]), cap)
# Lichtfleck: radial bei 36%/26%, 35% Weiß → 0 bei 55% des Farthest-Corner-Radius
hx, hy = CX + CD * 0.36, CY + CD * 0.26
far = max(math.hypot(hx - cx, hy - cy) for cx in (CX, CX + CD) for cy in (CY, CY + CD))
t = np.clip(np.hypot(XX - hx, YY - hy) / (far * 0.55), 0, 1)
paint((1, 1, 1, 1.0), cap * (0.35 * (1 - t)))
inset_shadow(cap, 0, -4, 7, oklch(0.4, 0.02, 260, 0.25))
inset_shadow(cap, 0, 2, 3, (1, 1, 1, 0.50))

# Zeiger: 12×34, Radius 6, um 55° gedreht um die Reglermitte
bar = Image.fromarray((rrect(KX + 56, KY + 14, 12, 34, 6) * 255).astype(np.uint8))
bar = bar.rotate(-55, center=(128 * SS, 128 * SS), resample=Image.BICUBIC)  # PIL dreht gegen den Uhrzeigersinn
paint(LINE, np.asarray(bar, dtype=np.float32) / 255.0)

# Auf Icon-Form zuschneiden
canvas[..., 3] *= icon

# ---------- Export ----------
if __name__ == "__main__":
    out_dir = sys.argv[1] if len(sys.argv) > 1 else "."
    os.makedirs(out_dir, exist_ok=True)
    big = Image.fromarray((np.clip(canvas, 0, 1) * 255 + 0.5).astype(np.uint8), "RGBA")
    for s in (256, 128, 64, 32, 16):
        path = os.path.join(out_dir, f"icon_3a_{s}.png")
        big.resize((s, s), Image.LANCZOS).save(path)
        print("geschrieben:", path)
