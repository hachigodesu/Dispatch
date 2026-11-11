"""Draws Dispatch's app icon (an amber rounded square with a white double chevron, "send onward")
and writes src/Dispatch.App/Assets/dispatch.ico with 16, 24, 32, 48, 64, 128 and 256 px images.

Pure Python (no Pillow needed):  python -I tools/make-icon.py
"""
import pathlib
import struct
import zlib

ROOT = pathlib.Path(__file__).resolve().parent.parent
OUT = ROOT / "src" / "Dispatch.App" / "Assets" / "dispatch.ico"
BG = (0xE0, 0x8A, 0x1E)       # amber
BG_EDGE = (0xB8, 0x6A, 0x10)  # darker rim
FG = (0xFF, 0xFF, 0xFF)


def inside_rounded(x, y, r):
    # Square 0..1 with corner radius r.
    cx = min(max(x, r), 1 - r)
    cy = min(max(y, r), 1 - r)
    return (x - cx) ** 2 + (y - cy) ** 2 <= r * r


def dist_to_segment(px, py, ax, ay, bx, by):
    dx, dy = bx - ax, by - ay
    t = max(0.0, min(1.0, ((px - ax) * dx + (py - ay) * dy) / (dx * dx + dy * dy)))
    qx, qy = ax + t * dx, ay + t * dy
    return ((px - qx) ** 2 + (py - qy) ** 2) ** 0.5


def chevron(x, y, offset, half_width):
    # A ">" made of two strokes meeting at (0.62 + offset, 0.5).
    tip = (0.60 + offset, 0.5)
    a = (0.36 + offset, 0.26)
    b = (0.36 + offset, 0.74)
    return min(dist_to_segment(x, y, *a, *tip), dist_to_segment(x, y, *b, *tip)) <= half_width


def pixel(x, y):
    if not inside_rounded(x, y, 0.22):
        return None
    if chevron(x, y, -0.12, 0.062) or chevron(x, y, 0.12, 0.062):
        return FG
    rim = not inside_rounded((x - 0.5) * 1.06 + 0.5, (y - 0.5) * 1.06 + 0.5, 0.22)
    return BG_EDGE if rim else BG


def render(size, ss=4):
    rows = []
    for py in range(size):
        row = bytearray()
        for px in range(size):
            acc = [0, 0, 0, 0]
            for sy in range(ss):
                for sx in range(ss):
                    c = pixel((px + (sx + 0.5) / ss) / size, (py + (sy + 0.5) / ss) / size)
                    if c is not None:
                        acc[0] += c[0]; acc[1] += c[1]; acc[2] += c[2]; acc[3] += 255
            n = ss * ss
            alpha = acc[3] // n
            if alpha:
                row += bytes((acc[0] * 255 // acc[3], acc[1] * 255 // acc[3], acc[2] * 255 // acc[3], alpha))
            else:
                row += b"\0\0\0\0"
        rows.append(bytes(row))
    return rows


def png(size):
    rows = render(size)
    raw = b"".join(b"\0" + r for r in rows)

    def chunk(tag, data):
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))


def main():
    sizes = [16, 24, 32, 48, 64, 128, 256]
    images = [png(s) for s in sizes]
    header = struct.pack("<HHH", 0, 1, len(sizes))
    offset = 6 + 16 * len(sizes)
    entries = b""
    for s, data in zip(sizes, images):
        entries += struct.pack("<BBBBHHII", s % 256, s % 256, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_bytes(header + entries + b"".join(images))
    (OUT.parent / "dispatch-256.png").write_bytes(images[-1])
    print(f"wrote {OUT.relative_to(ROOT)} ({OUT.stat().st_size} bytes)")


if __name__ == "__main__":
    main()
