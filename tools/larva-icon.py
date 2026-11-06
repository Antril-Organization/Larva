"""Draws Larva's icon (Larva.Setup/larva.ico): the mark of tools/larva-logo.py (a green worm) on its dark rounded tile,
at every size Windows asks for, antialiased.

    python -I tools/larva-icon.py Larva.Setup/larva.ico

The wizard draws the icon itself (Wizard.cs); brand/larva-app.svg is the same picture as a vector.
"""
import bisect
import importlib.util
import os
import struct
import sys

spec = importlib.util.spec_from_file_location("larva_logo", os.path.join(os.path.dirname(os.path.abspath(__file__)), "larva-logo.py"))
logo = importlib.util.module_from_spec(spec)
sys.argv, saved = [sys.argv[0]], sys.argv
spec.loader.exec_module(logo)
sys.argv = saved


def rgb(text):
    return tuple(int(text[i:i + 2], 16) for i in (1, 3, 5))


TILE, FROM, TO, MARK, EYE = (rgb(c) for c in (logo.TILE, logo.BODY_FROM, logo.BODY_TO, logo.MARK, logo.EYE_COLOR))
SX, SY = logo.SHIFT
OUTLINE = [(x + SX, y + SY) for x, y in logo.OUTLINE]
EDGES = [(OUTLINE[i], OUTLINE[(i + 1) % len(OUTLINE)]) for i in range(len(OUTLINE))]
MIN_X, MAX_X = min(x for x, _ in OUTLINE), max(x for x, _ in OUTLINE)
MIN_Y, MAX_Y = min(y for _, y in OUTLINE), max(y for _, y in OUTLINE)
MARKS = [((a[0] + SX, a[1] + SY), (b[0] + SX, b[1] + SY)) for a, b in logo.MARKS]
EX, EY, ER = logo.EYE[0] + SX, logo.EYE[1] + SY, logo.EYE[2]
CORNER = 56


def crossings(y):
    """Where the body's outline crosses the line at height y, sorted."""
    xs = []
    for (x0, y0), (x1, y1) in EDGES:
        if (y0 <= y) != (y1 <= y):
            xs.append(x0 + (y - y0) * (x1 - x0) / (y1 - y0))
    xs.sort()
    return xs


def in_tile(x, y):
    cx = min(max(x, CORNER), 256 - CORNER)
    cy = min(max(y, CORNER), 256 - CORNER)
    return 0 <= x <= 256 and 0 <= y <= 256 and (x - cx) ** 2 + (y - cy) ** 2 <= CORNER * CORNER


def near_mark(x, y):
    for (ax, ay), (bx, by) in MARKS:
        dx, dy = bx - ax, by - ay
        t = max(0.0, min(1.0, ((x - ax) * dx + (y - ay) * dy) / (dx * dx + dy * dy)))
        if (x - ax - t * dx) ** 2 + (y - ay - t * dy) ** 2 <= 2.25 ** 2:
            return True
    return False


def mix(a, b, t):
    return tuple(a[i] + (b[i] - a[i]) * t for i in range(3))


def color(x, y, inside):
    """The color at (x, y) on the 256 canvas, or None outside the tile."""
    if not in_tile(x, y):
        return None
    if (x - EX) ** 2 + (y - EY) ** 2 <= ER * ER:
        return EYE
    if inside:
        # The gradient runs from the body's lower left to its upper right.
        t = ((x - MIN_X) / (MAX_X - MIN_X) + 1 - (y - MIN_Y) / (MAX_Y - MIN_Y)) / 2
        body = mix(FROM, TO, max(0.0, min(1.0, t)))
        return mix(body, MARK, 0.55) if near_mark(x, y) else body
    return TILE


def image(size, grid=4):
    """BGRA rows, bottom-up (as an icon's bitmap stores them)."""
    scale = 256 / size
    rows = []
    for py in range(size - 1, -1, -1):
        sums = [[0.0, 0.0, 0.0, 0] for _ in range(size)]
        for sy in range(grid):
            y = (py + (sy + 0.5) / grid) * scale
            xs = crossings(y)
            for px in range(size):
                for sx in range(grid):
                    x = (px + (sx + 0.5) / grid) * scale
                    c = color(x, y, bisect.bisect_left(xs, x) % 2 == 1)
                    if c:
                        s = sums[px]
                        s[0] += c[0]; s[1] += c[1]; s[2] += c[2]; s[3] += 1
        row = bytearray()
        for r, g, b, a in sums:
            if a:
                row += bytes((round(b / a), round(g / a), round(r / a), 255 * a // (grid * grid)))
            else:
                row += bytes(4)
        rows.append(bytes(row))
    return rows


def entry(size):
    rows = image(size)
    header = struct.pack("<IiiHHIIiiII", 40, size, size * 2, 1, 32, 0, 0, 0, 0, 0, 0)
    mask = bytes(((size + 31) // 32 * 4) * size)  # the alpha channel decides; the AND mask is empty
    return header + b"".join(rows) + mask


def main(out):
    sizes = [16, 20, 24, 32, 40, 48, 64, 128]
    images = [entry(s) for s in sizes]
    data = struct.pack("<HHH", 0, 1, len(sizes))
    offset = 6 + 16 * len(sizes)
    for size, body in zip(sizes, images):
        data += struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32, len(body), offset)
        offset += len(body)
    data += b"".join(images)
    with open(out, "wb") as f:
        f.write(data)


if __name__ == "__main__":
    main(sys.argv[1])
