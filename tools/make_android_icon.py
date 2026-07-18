"""Composite a clean Android launcher icon from the channel-encoded icon template.
The template packs A=silhouette, R=dragon, G=tick-circle, B=tick-check; showing it raw
put a green 'dot' (the tick circle) on the launcher. We render just the dragon in the
brand green on a dark rounded background — no tick — as a proper legacy + adaptive icon."""
from PIL import Image, ImageDraw
import sys

SRC = sys.argv[1]           # icon-template-256.png
OUT_LEGACY = sys.argv[2]    # drawable/icon.png (full, masked by launcher)
OUT_FORE = sys.argv[3]      # adaptive foreground (dragon, safe-zone padded, transparent)
OUT_BACK = sys.argv[4]      # adaptive background (solid dark)

GREEN = (0x3D, 0xAF, 0x7E)
DARK  = (0x0F, 0x13, 0x17)
N = 432  # adaptive icon canvas

tpl = Image.open(SRC).convert("RGBA")
w, h = tpl.size
px = tpl.load()

# Un-premultiply and pull the dragon (R) + alpha (A) masks; find the dragon bbox.
dragon = [[0.0]*w for _ in range(h)]
alpha  = [[0.0]*w for _ in range(h)]
minx, miny, maxx, maxy = w, h, -1, -1
for y in range(h):
    for x in range(w):
        r, g, b, a = px[x, y]
        d = r
        if 0 < a < 255:
            d = min(255, d * 255 // a)
        dragon[y][x] = d
        alpha[y][x]  = a
        if d > 30:
            minx = min(minx, x); maxx = max(maxx, x)
            miny = min(miny, y); maxy = max(maxy, y)

bcx = (minx + maxx) / 2.0
bcy = (miny + maxy) / 2.0
bw, bh = (maxx - minx + 1), (maxy - miny + 1)

def render(size, pad, bg, radius=None):
    """Green dragon fit to `size` with `pad` fraction margin, on optional bg."""
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    if bg is not None:
        base = Image.new("RGBA", (size, size), bg + (255,))
        if radius:
            mask = Image.new("L", (size, size), 0)
            ImageDraw.Draw(mask).rounded_rectangle([0, 0, size-1, size-1], radius=radius, fill=255)
            base.putalpha(mask)
        img = Image.alpha_composite(img, base)
    scale = size * (1 - 2 * pad) / max(bw, bh)
    cc = (size - 1) / 2.0
    out = img.load()
    for y in range(size):
        for x in range(size):
            sx = bcx + (x - cc) / scale
            sy = bcy + (y - cc) / scale
            ix, iy = int(round(sx)), int(round(sy))
            if 0 <= ix < w and 0 <= iy < h:
                d = dragon[iy][ix]; av = alpha[iy][ix]
                if d < 40:  # drop the faint silhouette halo so no ghost square shows
                    continue
                a = int(round(min(av, d)))
                if a > 0:
                    r0, g0, b0, a0 = out[x, y]
                    fa = a / 255.0
                    out[x, y] = (
                        int(GREEN[0]*fa + r0*(1-fa)),
                        int(GREEN[1]*fa + g0*(1-fa)),
                        int(GREEN[2]*fa + b0*(1-fa)),
                        max(a0, a),
                    )
    return img

# Legacy icon: dragon on a dark rounded square (launchers mask further).
render(N, 0.16, DARK, radius=int(N*0.22)).save(OUT_LEGACY)
# Adaptive foreground: dragon only, generous safe-zone padding, transparent bg.
render(N, 0.26, None).save(OUT_FORE)
# Adaptive background: solid dark.
Image.new("RGBA", (N, N), DARK + (255,)).save(OUT_BACK)
print("wrote icons")
