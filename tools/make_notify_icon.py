"""Generate the Android STATUS-BAR (notification small) icon from the channel-encoded template.

Android renders a notification's small icon as a silhouette: it keeps the ALPHA and repaints
every visible pixel white/grey. Handing it the launcher icon (a dragon on a dark rounded
square) therefore shows up as a plain white SQUARE in the status bar. The small icon must be
the dragon alone — white on full transparency, nothing else.

Template channels (same as make_android_icon.py): A=silhouette, R=dragon, G=tick-circle,
B=tick-check. Only R (+A) is used here; the tick and the background are dropped.

Usage:  python tools/make_notify_icon.py src/SplitGuard/Assets/icon-template-256.png \
                                        src/SplitGuard.Android/Resources
Writes drawable-{mdpi,hdpi,xhdpi,xxhdpi,xxxhdpi}/notify.png (24dp at each density).
"""
from PIL import Image
import os
import sys

SRC = sys.argv[1]           # icon-template-256.png
RES = sys.argv[2]           # .../Resources

# 24dp small icon at each density bucket (mdpi = 1x).
DENSITIES = {"mdpi": 24, "hdpi": 36, "xhdpi": 48, "xxhdpi": 72, "xxxhdpi": 96}
PAD = 0.08                  # slight inset so the glyph doesn't touch the 24dp box edges

tpl = Image.open(SRC).convert("RGBA")
w, h = tpl.size
px = tpl.load()

# Mask = R, but ONLY where the template is fully opaque (A=255, i.e. solidly inside the rounded
# square). Two traps this avoids, both of which drew a rounded-square ghost in the status bar:
#   * outside the silhouette the template still stores white (R=255) behind A=0, so keying on R
#     alone paints the four corner arcs;
#   * along the silhouette's antialiased rim A is mid-valued, so min(A,R) paints that rim as a
#     thin rounded outline.
# Inside the icon A is a flat 255, so the dragon keeps its own antialiasing via R.
dragon = [[0.0] * w for _ in range(h)]
minx, miny, maxx, maxy = w, h, -1, -1
for y in range(h):
    for x in range(w):
        r, g, b, a = px[x, y]
        m = r if a >= 250 else 0
        dragon[y][x] = m
        if m > 30:
            minx = min(minx, x); maxx = max(maxx, x)
            miny = min(miny, y); maxy = max(maxy, y)

bcx, bcy = (minx + maxx) / 2.0, (miny + maxy) / 2.0
bw, bh = (maxx - minx + 1), (maxy - miny + 1)


def render(size):
    """White dragon fit to `size` on transparency — no background, no tick."""
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    out = img.load()
    scale = size * (1 - 2 * PAD) / max(bw, bh)
    cc = (size - 1) / 2.0
    # Supersample: the status bar icon is tiny, so nearest-neighbour would alias badly.
    ss = 4
    acc = [[0.0] * size for _ in range(size)]
    for y in range(size):
        for x in range(size):
            total = 0.0
            for sy in range(ss):
                for sx in range(ss):
                    fx = x + (sx + 0.5) / ss - 0.5
                    fy = y + (sy + 0.5) / ss - 0.5
                    ix = int(round(bcx + (fx - cc) / scale))
                    iy = int(round(bcy + (fy - cc) / scale))
                    if 0 <= ix < w and 0 <= iy < h:
                        d = dragon[iy][ix]
                        if d >= 40:          # real dragon ink; the square's rim sits far below
                            total += d
            acc[y][x] = total / (ss * ss)
    for y in range(size):
        for x in range(size):
            a = int(round(acc[y][x]))
            if a > 0:
                out[x, y] = (255, 255, 255, min(255, a))
    return img


for bucket, size in DENSITIES.items():
    d = os.path.join(RES, f"drawable-{bucket}")
    os.makedirs(d, exist_ok=True)
    render(size).save(os.path.join(d, "notify.png"))
    print(f"wrote drawable-{bucket}/notify.png ({size}x{size})")
