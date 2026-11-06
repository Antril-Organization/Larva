"""Draws Larva's mark (brand/larva.svg, and brand/larva-app.svg on its app tile): a worm's silhouette, one
tapered body along an S curve (tail low left, head raised right), green.

    python -I tools/larva-logo.py brand
"""
import math
import sys

N = 200

def center(t):
    # An S lying on its side: low at the tail, a dip, a rise to the head.
    x = 36 + 170 * t
    y = 160 - 24 * math.sin(2.0 * math.pi * (t - 0.12)) - 30 * t * t
    return x, y

def radius(t):
    # Thin at the tail, full at the head.
    return 9 + 21 * (t ** 0.75)

def normal(t):
    e = 1e-4
    x0, y0 = center(max(0, t - e)); x1, y1 = center(min(1, t + e))
    dx, dy = x1 - x0, y1 - y0
    n = math.hypot(dx, dy)
    return -dy / n, dx / n

left, right = [], []
for i in range(N + 1):
    t = i / N
    (x, y), (nx, ny), r = center(t), normal(t), radius(t)
    left.append((x + nx * r, y + ny * r))
    right.append((x - nx * r, y - ny * r))

def arc(cx, cy, r, a0, a1, steps=24):
    return [(cx + r * math.cos(a0 + (a1 - a0) * k / steps), cy + r * math.sin(a0 + (a1 - a0) * k / steps)) for k in range(1, steps)]

# Round ends: the head's cap, then back along the other side, the tail's cap.
hx, hy = center(1); tx, ty = center(0)
hn = normal(1); tn = normal(0)
head_angle = math.atan2(hn[1], hn[0])
tail_angle = math.atan2(-tn[1], -tn[0])
outline = left + arc(hx, hy, radius(1), head_angle, head_angle - math.pi) + right[::-1] + arc(tx, ty, radius(0), tail_angle, tail_angle - math.pi)
path = "M" + " L".join(f"{x:.1f},{y:.1f}" for x, y in outline) + " Z"

# Segment marks: short lines across the body.
marks = []
for t in (0.34, 0.48, 0.62, 0.76):
    (x, y), (nx, ny), r = center(t), normal(t), radius(t) * 0.58
    marks.append(f'M{x + nx * r:.1f},{y + ny * r:.1f} L{x - nx * r:.1f},{y - ny * r:.1f}')

def svg(background):
    out = ['<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256" width="256" height="256">', '  <title>Larva</title>',
           '  <defs>', '    <linearGradient id="larva-body" x1="0" y1="1" x2="1" y2="0">',
           '      <stop offset="0" stop-color="#2E9E44"/>', '      <stop offset="1" stop-color="#7BD94F"/>',
           '    </linearGradient>', '  </defs>']
    if background:
        out.append('  <rect width="256" height="256" rx="56" fill="#0F2A1C"/>')
    xs = [x for x, _ in outline]; ys = [y for _, y in outline]
    dx = 128 - (min(xs) + max(xs)) / 2; dy = 128 - (min(ys) + max(ys)) / 2
    out.append(f'  <g transform="translate({dx:.1f} {dy:.1f})">')
    out.append(f'  <path d="{path}" fill="url(#larva-body)"/>')
    out.append(f'  <path d="{" ".join(marks)}" stroke="#1E6B33" stroke-width="4.5" stroke-linecap="round" fill="none" opacity="0.55"/>')
    ex, ey = hx + 6, hy - 9
    out.append(f'  <circle cx="{ex:.1f}" cy="{ey:.1f}" r="5.5" fill="#0F2A1C"/>')
    out.append('  </g>')
    out.append('</svg>')
    return "\n".join(out) + "\n"

# The mark's parts, for tools/larva-icon.py (which draws the same in an .ico): the body's outline, the marks' ends,
# the eye, and the shift that centers them (all on the 256 x 256 canvas).
MARKS = []
for t in (0.34, 0.48, 0.62, 0.76):
    (x, y), (nx, ny), r = center(t), normal(t), radius(t) * 0.58
    MARKS.append(((x + nx * r, y + ny * r), (x - nx * r, y - ny * r)))
EYE = (hx + 6, hy - 9, 5.5)
SHIFT = (128 - (min(x for x, _ in outline) + max(x for x, _ in outline)) / 2, 128 - (min(y for _, y in outline) + max(y for _, y in outline)) / 2)
OUTLINE = outline
TILE, BODY_FROM, BODY_TO, MARK, EYE_COLOR = "#0F2A1C", "#2E9E44", "#7BD94F", "#1E6B33", "#0F2A1C"

if __name__ == "__main__":
    folder = sys.argv[1]
    open(f"{folder}/larva.svg", "w", encoding="utf-8").write(svg(False))
    open(f"{folder}/larva-app.svg", "w", encoding="utf-8").write(svg(True))
