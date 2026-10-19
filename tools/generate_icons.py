"""Build the two Windows application icons without external packages."""

from pathlib import Path
import math
import struct
import zlib

ROOT = Path(__file__).resolve().parents[1]


def png_chunk(kind, data):
    return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data))


def png(size, kind):
    rows = bytearray()
    scale = size / 64
    for y in range(size):
        rows.append(0)
        for x in range(size):
            px, py = (x + 0.5) / scale, (y + 0.5) / scale
            dx = max(abs(px - 32) - 22, 0)
            dy = max(abs(py - 32) - 22, 0)
            edge = math.hypot(dx, dy) - 9
            alpha = max(0, min(255, int((0.5 - edge) * 255)))
            base = (31, 92, 99) if kind == "admin" else (143, 79, 71)
            # A clean clock mark remains legible in the taskbar at 16 pixels.
            radius = math.hypot(px - 32, py - 32)
            ring = 14.5 <= radius <= 17.5
            hand_up = abs(px - 32) <= 1.65 and 23 <= py <= 33
            hand_right = abs(py - 32) <= 1.65 and 31 <= px <= 40
            center = radius <= 2.4
            mark = ring or hand_up or hand_right or center
            if kind == "client" and 40 <= px <= 49 and 40 <= py <= 49 and math.hypot(px - 44.5, py - 44.5) < 5:
                color = (255, 211, 150)
            else:
                color = (247, 248, 244) if mark else base
            rows.extend((*color, alpha))
    header = b"\x89PNG\r\n\x1a\n"
    header += png_chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
    return header + png_chunk(b"IDAT", zlib.compress(bytes(rows), 9)) + png_chunk(b"IEND", b"")


for role in ("admin", "client"):
    frames = [(size, png(size, role)) for size in (32, 64, 256)]
    data = bytearray(struct.pack("<HHH", 0, 1, len(frames)))
    offset = 6 + len(frames) * 16
    for size, frame in frames:
        data.extend(struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32, len(frame), offset))
        offset += len(frame)
    for _, frame in frames:
        data.extend(frame)
    target = ROOT / ("ParentControl.Admin" if role == "admin" else "ParentControl.ClientApp") / "app.ico"
    target.write_bytes(data)
    print(target)
