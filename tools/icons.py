"""SVG pictures as icons, drawn by Edge (headless) and cut out of one sheet: Windows .ico files (PNG images inside),
and the PNGs and .icns that Linux and macOS take.

    python -I tools/icons.py ico  out.ico  picture.svg [small.svg]   (small.svg: what 16 to 24 pixels show)
    python -I tools/icons.py png  folder   picture.svg   (folder/16.png … folder/256.png: Linux icon themes)
    python -I tools/icons.py icns out.icns picture.svg   (macOS)

Needs Microsoft Edge (Windows); every size is drawn from the SVG itself, not scaled down from a big one.
"""
import base64
import os
import struct
import subprocess
import sys
import tempfile
import time
import zlib

SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]
EDGE = os.path.join(os.environ.get("ProgramFiles(x86)", r"C:\Program Files (x86)"), r"Microsoft\Edge\Application\msedge.exe")


def render(svg_text, sizes):
    """{size: (width, height, RGBA bytes)} for each size, drawn by Edge on a transparent sheet."""
    gap = 8
    width = sum(sizes) + gap * (len(sizes) + 1)
    height = max(sizes) + 2 * gap
    data = base64.b64encode(svg_text.encode("utf-8")).decode()
    places, x = {}, gap
    imgs = []
    for s in sizes:
        places[s] = (x, gap)
        imgs.append(f'<img src="data:image/svg+xml;base64,{data}" style="position:absolute;left:{x}px;top:{gap}px;width:{s}px;height:{s}px">')
        x += s + gap
    html = f"<!doctype html><html><body style='margin:0;background:transparent'>{''.join(imgs)}</body></html>"
    with tempfile.TemporaryDirectory(ignore_cleanup_errors=True) as work:  # Edge may still hold its profile
        page = os.path.join(work, "sheet.html")
        shot = os.path.join(work, "sheet.png")
        with open(page, "w", encoding="utf-8") as f:
            f.write(html)
        # A profile of its own: a browser already open would otherwise take the job and draw nothing here.
        subprocess.run([EDGE, "--headless=new", f"--user-data-dir={os.path.join(work, 'profile')}", "--no-first-run", "--disable-gpu", "--hide-scrollbars", "--force-device-scale-factor=1",
                        "--default-background-color=00000000", f"--screenshot={shot}", f"--window-size={width},{height}",
                        "file:///" + page.replace("\\", "/")], check=True, capture_output=True, timeout=60)
        # Edge hands the job to a process of its own and returns: the picture comes a moment later.
        for _ in range(300):
            if os.path.exists(shot) and os.path.getsize(shot) > 0:
                time.sleep(0.3)
                break
            time.sleep(0.1)
        w, h, pixels = read_png(open(shot, "rb").read())
    out = {}
    for s in sizes:
        px, py = places[s]
        rows = [pixels[(py + r) * w * 4 + px * 4:(py + r) * w * 4 + (px + s) * 4] for r in range(s)]
        out[s] = (s, s, b"".join(rows))
    return out


def read_png(data):
    """Width, height and RGBA bytes of an 8-bit RGBA PNG (what Edge writes)."""
    assert data[:8] == b"\x89PNG\r\n\x1a\n"
    pos, idat, width = 8, b"", 0
    while pos < len(data):
        length, kind = struct.unpack(">I4s", data[pos:pos + 8])
        chunk = data[pos + 8:pos + 8 + length]
        if kind == b"IHDR":
            width, height, depth, color = struct.unpack(">IIBB", chunk[:10])
            assert depth == 8 and color == 6, "an 8-bit RGBA PNG"
        elif kind == b"IDAT":
            idat += chunk
        pos += 12 + length
    raw = zlib.decompress(idat)
    stride = width * 4
    out = bytearray(stride * height)
    previous = bytearray(stride)
    i = 0
    for y in range(height):
        kind = raw[i]
        line = bytearray(raw[i + 1:i + 1 + stride])
        i += 1 + stride
        for x in range(stride):
            a = line[x - 4] if x >= 4 else 0
            b = previous[x]
            c = previous[x - 4] if x >= 4 else 0
            if kind == 1:
                line[x] = (line[x] + a) & 255
            elif kind == 2:
                line[x] = (line[x] + b) & 255
            elif kind == 3:
                line[x] = (line[x] + (a + b) // 2) & 255
            elif kind == 4:
                p = a + b - c
                pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
                line[x] = (line[x] + (a if pa <= pb and pa <= pc else b if pb <= pc else c)) & 255
        out[y * stride:(y + 1) * stride] = line
        previous = line
    return width, height, bytes(out)


def write_png(width, height, rgba):
    def chunk(kind, body):
        return struct.pack(">I", len(body)) + kind + body + struct.pack(">I", zlib.crc32(kind + body) & 0xFFFFFFFF)
    raw = b"".join(b"\x00" + rgba[y * width * 4:(y + 1) * width * 4] for y in range(height))
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))


def ico(images):
    """An .ico of PNG images (Windows Vista and later read them)."""
    pngs = [(s, write_png(*images[s])) for s in sorted(images)]
    out = struct.pack("<HHH", 0, 1, len(pngs))
    offset = 6 + 16 * len(pngs)
    for s, png in pngs:
        out += struct.pack("<BBBBHHII", s % 256, s % 256, 0, 0, 1, 32, len(png), offset)
        offset += len(png)
    return out + b"".join(p for _, p in pngs)


ICNS_SIZES = [16, 32, 64, 128, 256, 512, 1024]


def icns(images):
    """A macOS .icns of PNG images, Retina ones (@2x) included: ICNS_SIZES drawn."""
    kinds = [(b"ic04", 16), (b"ic05", 32), (b"ic11", 32), (b"ic12", 64), (b"ic07", 128), (b"ic08", 256), (b"ic13", 256),
             (b"ic09", 512), (b"ic14", 512), (b"ic10", 1024)]
    pngs = {}
    body = b""
    for kind, size in kinds:
        if size in images:
            png = pngs.setdefault(size, write_png(*images[size]))
            body += kind + struct.pack(">I", 8 + len(png)) + png
    return b"icns" + struct.pack(">I", 8 + len(body)) + body


if __name__ == "__main__":
    mode, target, picture = sys.argv[1:4]
    images = render(open(picture, encoding="utf-8").read(), ICNS_SIZES if mode == "icns" else SIZES)
    if len(sys.argv) > 4:
        small = [s for s in images if s <= 24]
        images.update(render(open(sys.argv[4], encoding="utf-8").read(), small))
    if mode == "ico":
        open(target, "wb").write(ico(images))
    elif mode == "icns":
        open(target, "wb").write(icns(images))
    elif mode == "png":
        os.makedirs(target, exist_ok=True)
        for s, image in images.items():
            open(os.path.join(target, f"{s}.png"), "wb").write(write_png(*image))
    else:
        sys.exit("usage: icons.py ico|png|icns <out> <picture.svg>")
