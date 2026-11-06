"""Draws the icons of Larva's files as SVG (brand/): a page with a soft shadow, its corner folded in the kind's color,
the kind's mark on it and a ribbon with its extension.

    python -I tools/file-icons.py brand

- file-lva.svg     Larva libraries (.lva): the worm, green ribbon "LVA".
- file-lvo.svg     Larva object files (.lvo): the worm, teal ribbon "LVO".
- file-lvm.svg     Larva modules (.lvm): the worm, olive ribbon "LVM".
- file-bind.svg    binding descriptions (.bind): the worm, blue ribbon "BIND".
- file-hive.svg    Hive packages (.hive): an ant colony, its mound cut open (tunnels, chambers with eggs and a larva,
                   red ants at work), amber ribbon "HIVE".
- file-queen.svg   projects (.queen): the queen's crown alone (no page): gold, pearls, a ruby and two emeralds.

Each has a -small.svg (16 to 24 pixels, where a page would leave the mark too small to see): the mark alone.
tools/icons.py turns them into .ico and .icns files.
"""
import importlib.util
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
spec = importlib.util.spec_from_file_location("larva_logo", os.path.join(HERE, "larva-logo.py"))
logo = importlib.util.module_from_spec(spec)
saved, sys.argv = sys.argv, [sys.argv[0]]
spec.loader.exec_module(logo)
sys.argv = saved

ANT_RED = "#DC143C"
HEAD = '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256" width="256" height="256">\n'


def shade(color, amount):
    """The color lighter (amount > 0, toward white) or darker (amount < 0, toward black)."""
    r, g, b = (int(color[i:i + 2], 16) for i in (1, 3, 5))
    target = 255 if amount > 0 else 0
    return "#" + "".join(f"{round(c + (target - c) * abs(amount)):02X}" for c in (r, g, b))


def page(mark, mark_defs, accent, label):
    """The page: shadow, paper, the folded corner and the ribbon in `accent`, the mark between them."""
    defs = f"""    <filter id="shadow" x="-20%" y="-20%" width="140%" height="140%">
      <feDropShadow dx="0" dy="5" stdDeviation="5" flood-color="#000000" flood-opacity="0.28"/>
    </filter>
    <linearGradient id="paper" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#FFFFFF"/>
      <stop offset="1" stop-color="#EDF1EE"/>
    </linearGradient>
    <linearGradient id="fold" x1="0" y1="1" x2="1" y2="0">
      <stop offset="0" stop-color="{shade(accent, 0.25)}"/>
      <stop offset="1" stop-color="{shade(accent, 0.6)}"/>
    </linearGradient>
    <linearGradient id="ribbon" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="{shade(accent, 0.12)}"/>
      <stop offset="1" stop-color="{shade(accent, -0.12)}"/>
    </linearGradient>
{mark_defs}"""
    size = 34 if len(label) <= 3 else 30
    body = f"""  <g filter="url(#shadow)">
    <path d="M58 10 H166 L218 62 V234 Q218 246 206 246 H58 Q46 246 46 234 V22 Q46 10 58 10 Z" fill="url(#paper)" stroke="#C3CCC6" stroke-width="3"/>
  </g>
  <path d="M166 10 V50 Q166 62 178 62 H218 Z" fill="url(#fold)" stroke="{shade(accent, -0.1)}" stroke-width="3" stroke-linejoin="round"/>
{mark}
  <path d="M30 236 L46 246 V236 Z" fill="{shade(accent, -0.45)}"/>
  <rect x="30" y="186" width="158" height="50" rx="9" fill="url(#ribbon)"/>
  <rect x="30" y="186" width="158" height="25" rx="9" fill="#FFFFFF" opacity="0.12"/>
  <text x="109" y="{223 if size == 34 else 221}" text-anchor="middle" font-family="Segoe UI, Arial, Helvetica, sans-serif" font-weight="800" font-size="{size}" letter-spacing="1.5" fill="#FFFFFF">{label}</text>"""
    return HEAD + "  <defs>\n" + defs + "\n  </defs>\n" + body + "\n</svg>\n"


def alone(mark, mark_defs, x, y, scale):
    """The mark by itself, centered at (x, y) and `scale` times bigger: what 16 to 24 pixels show."""
    return (HEAD + (f"  <defs>\n{mark_defs}\n  </defs>\n" if mark_defs else "")
            + f'  <g transform="translate(128 128) scale({scale}) translate({-x} {-y})">\n{mark}\n  </g>\n</svg>\n')


# ── The worm ───────────────────────────────────────

def worm_mark(cx=128, cy=112, scale=0.66):
    """brand/larva.svg's worm, with a soft highlight along its back and a shadow under it."""
    sx, sy = logo.SHIFT

    def at(x, y):
        return f"{(x + sx - 128) * scale + cx:.1f},{(y + sy - 128) * scale + cy:.1f}"
    outline = " L".join(at(x, y) for x, y in logo.OUTLINE)
    marks = " ".join(f"M{at(*a)} L{at(*b)}" for a, b in logo.MARKS)
    ex, ey, er = logo.EYE
    defs = """    <linearGradient id="worm" x1="0" y1="1" x2="1" y2="0">
      <stop offset="0" stop-color="#2A9440"/>
      <stop offset="1" stop-color="#86E05A"/>
    </linearGradient>
    <linearGradient id="sheen" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#FFFFFF" stop-opacity="0.45"/>
      <stop offset="0.5" stop-color="#FFFFFF" stop-opacity="0"/>
    </linearGradient>"""
    mark = (f'  <ellipse cx="{cx}" cy="{cy + 32 * scale / 0.66:.1f}" rx="{70 * scale / 0.66:.1f}" ry="{7 * scale / 0.66:.1f}" fill="#000000" opacity="0.10"/>\n'
            f'  <path d="M{outline} Z" fill="url(#worm)" stroke="#1E6B33" stroke-width="2.5" stroke-linejoin="round"/>\n'
            f'  <path d="M{outline} Z" fill="url(#sheen)"/>\n'
            + (f'  <path d="{marks}" stroke="#1E6B33" stroke-width="4" stroke-linecap="round" fill="none" opacity="0.5"/>\n' if marks else "")
            + f'  <circle cx="{at(ex, ey).split(",")[0]}" cy="{at(ex, ey).split(",")[1]}" r="{er * scale + 0.8:.1f}" fill="#0F2A1C"/>\n'
            f'  <circle cx="{float(at(ex, ey).split(",")[0]) + 1.4:.1f}" cy="{float(at(ex, ey).split(",")[1]) - 1.4:.1f}" r="1.3" fill="#FFFFFF"/>')
    return mark, defs


LARVA_FILES = [
    ("file-lva", "LVA", "#2E9E44"),
    ("file-lvo", "LVO", "#1B7F6B"),
    ("file-lvm", "LVM", "#6A8F1E"),
    ("file-bind", "BIND", "#2F6FB0"),
]


# ── The colony ─────────────────────────────────────

def ant(x, y, angle, scale=1.0):
    """A small red ant (the language's ant, in miniature), heading `angle` degrees (0: up)."""
    legs = " ".join(f"M0 {dy} L{side * 5.5} {dy + ty}" for dy, ty in ((-2, -3), (0, 1), (2, 4.5)) for side in (-1, 1))
    return (f'  <g transform="translate({x} {y}) rotate({angle}) scale({scale})" fill="{ANT_RED}">'
            f'<path d="{legs} M-1 -9.5 L-3.5 -13 M1 -9.5 L3.5 -13" stroke="{ANT_RED}" stroke-width="1.6" stroke-linecap="round" fill="none"/>'
            '<circle cx="0" cy="-7.5" r="3.4"/><ellipse cx="0" cy="-1" rx="2.8" ry="3.4"/><ellipse cx="0" cy="6.8" rx="4.4" ry="5.8"/></g>')


def hive_mark():
    defs = """    <linearGradient id="mound" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#F0BE72"/>
      <stop offset="1" stop-color="#B8702C"/>
    </linearGradient>
    <radialGradient id="chamber" cx="0.5" cy="0.4" r="0.6">
      <stop offset="0" stop-color="#5A3518"/>
      <stop offset="1" stop-color="#3A200A"/>
    </radialGradient>"""
    eggs = "".join(f'<ellipse cx="{x}" cy="{y}" rx="3.6" ry="2.6" fill="#FFF4DC" transform="rotate({r} {x} {y})"/>'
                   for x, y, r in ((88, 121, -20), (96, 123, 10), (93, 117, 30), (122, 157, 0), (130, 158, 15), (138, 156, -10)))
    grass = "".join(f'<path d="M{x} 166 Q{x + dx} {166 - h * 0.6} {x + dx * 1.6} {166 - h}" stroke="#4FA83A" stroke-width="3" stroke-linecap="round" fill="none"/>'
                    for x, dx, h in ((60, -4, 14), (64, 1, 18), (68, 5, 12), (190, -5, 12), (194, 0, 18), (198, 4, 14)))
    mark = f"""  <ellipse cx="128" cy="167" rx="78" ry="7" fill="#000000" opacity="0.12"/>
  <path d="M60 166 C64 116 96 64 128 58 C160 64 192 116 196 166 Z" fill="url(#mound)" stroke="#8A4E1C" stroke-width="3.5" stroke-linejoin="round"/>
  <path d="M82 158 C84 120 102 88 122 70 C110 94 100 124 98 158 Z" fill="#FFFFFF" opacity="0.2"/>
  <path d="M128 66 C125 86 132 98 128 112 C124 126 132 138 128 150 M128 100 C116 102 106 106 98 114 M129 126 C142 128 152 132 160 138" stroke="#5A3214" stroke-width="7" stroke-linecap="round" fill="none"/>
  <ellipse cx="128" cy="64" rx="10" ry="5" fill="#3A200A"/>
  <ellipse cx="93" cy="120" rx="18" ry="10" fill="url(#chamber)"/>
  <ellipse cx="163" cy="141" rx="18" ry="10" fill="url(#chamber)"/>
  <ellipse cx="130" cy="156" rx="21" ry="8.5" fill="url(#chamber)"/>
  {eggs}
  <path d="M157 143 C155 137 163 134 168 138 C171 141 169 145 165 145" stroke="#FFF4DC" stroke-width="4" stroke-linecap="round" fill="none"/>
  {grass}
{ant(108, 60, 75, 1.3)}
{ant(178, 108, -35, 1.3)}"""
    return mark, defs


# ── The queen ──────────────────────────────────────

def queen():
    # The colony's queen by her crown alone, filling the square (no page): gold with three points and their pearls, a
    # band with a ruby (the red of the language's ant) between two emeralds (the worm's green).
    defs = """  <defs>
    <linearGradient id="gold" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#FFE78A"/>
      <stop offset="0.55" stop-color="#F2B92A"/>
      <stop offset="1" stop-color="#C98A10"/>
    </linearGradient>
    <linearGradient id="band" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#F7C843"/>
      <stop offset="1" stop-color="#B57A08"/>
    </linearGradient>
    <radialGradient id="pearl" cx="0.35" cy="0.35" r="0.7">
      <stop offset="0" stop-color="#FFFFFF"/>
      <stop offset="0.5" stop-color="#FFF1C4"/>
      <stop offset="1" stop-color="#E0B44A"/>
    </radialGradient>
    <radialGradient id="ruby" cx="0.35" cy="0.3" r="0.75">
      <stop offset="0" stop-color="#FF7A90"/>
      <stop offset="0.45" stop-color="#DC143C"/>
      <stop offset="1" stop-color="#8E0B26"/>
    </radialGradient>
    <radialGradient id="emerald" cx="0.35" cy="0.3" r="0.75">
      <stop offset="0" stop-color="#B8F59A"/>
      <stop offset="0.45" stop-color="#2E9E44"/>
      <stop offset="1" stop-color="#145A26"/>
    </radialGradient>
  </defs>
"""
    gem = '<ellipse cx="{x}" cy="{y}" rx="{rx}" ry="{ry}" fill="url(#{kind})" stroke="#7A4E04" stroke-width="3"/>'
    body = (
        '  <ellipse cx="128" cy="226" rx="96" ry="9" fill="#000000" opacity="0.16"/>\n'
        # The crown's points, then its highlight along the left edges.
        '  <path d="M38 182 L26 78 L84 132 L128 52 L172 132 L230 78 L218 182 Z" fill="url(#gold)" stroke="#8A5A06" stroke-width="6" stroke-linejoin="round"/>\n'
        '  <path d="M42 170 L34 96 L80 140 M128 66 L96 126" stroke="#FFFFFF" stroke-width="5" stroke-linecap="round" fill="none" opacity="0.45"/>\n'
        # The band.
        '  <path d="M34 176 Q128 160 222 176 L222 214 Q128 198 34 214 Z" fill="url(#band)" stroke="#8A5A06" stroke-width="6" stroke-linejoin="round"/>\n'
        '  <path d="M44 182 Q128 168 212 182" stroke="#FFFFFF" stroke-width="4" stroke-linecap="round" fill="none" opacity="0.4"/>\n'
        # The jewels.
        + "  " + gem.format(x=128, y=190, rx=16, ry=13, kind="ruby") + "\n"
        + "  " + gem.format(x=76, y=195, rx=10, ry=9, kind="emerald") + "\n"
        + "  " + gem.format(x=180, y=195, rx=10, ry=9, kind="emerald") + "\n"
        + "  " + "".join(f'<circle cx="{x}" cy="{y}" r="{r}" fill="url(#pearl)" stroke="#8A5A06" stroke-width="4"/>'
                         for x, y, r in ((26, 72, 14), (128, 44, 16), (230, 72, 14))) + "\n"
        '  <circle cx="122" cy="185" r="3.5" fill="#FFFFFF" opacity="0.8"/>')
    return HEAD + defs + body + "\n</svg>\n"


def icons():
    """{file name: SVG text} of every icon."""
    out = {}
    worm, worm_defs = worm_mark()
    small_worm, small_defs = worm_mark(128, 128, 1.0)
    for name, label, accent in LARVA_FILES:
        out[name + ".svg"] = page(worm, worm_defs, accent, label)
        out[name + "-small.svg"] = alone(small_worm, small_defs, 128, 128, 1.0)
    hive, hive_defs = hive_mark()
    out["file-hive.svg"] = page(hive, hive_defs, "#C77A2A", "HIVE")
    out["file-hive-small.svg"] = alone(hive, hive_defs, 128, 114, 1.7)
    out["file-queen.svg"] = queen()
    out["file-queen-small.svg"] = queen()
    return out


if __name__ == "__main__":
    folder = sys.argv[1]
    for name, text in icons().items():
        with open(os.path.join(folder, name), "w", encoding="utf-8") as f:
            f.write(text)
