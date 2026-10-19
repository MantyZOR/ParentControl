"""Generate the small, deterministic paper-grain tile used by the WPF UI."""

from pathlib import Path
import random
import struct
import zlib


size = 96
rng = random.Random(20260923)
rows = bytearray()
for _ in range(size):
    rows.append(0)
    for _ in range(size):
        value = rng.random()
        if value < 0.40:
            rows.extend((18, 42, 43, rng.randint(5, 14)))
        elif value < 0.68:
            rows.extend((255, 255, 250, rng.randint(5, 12)))
        else:
            rows.extend((0, 0, 0, 0))


def chunk(tag: bytes, body: bytes) -> bytes:
    return struct.pack(">I", len(body)) + tag + body + struct.pack(">I", zlib.crc32(tag + body))


png = b"\x89PNG\r\n\x1a\n"
png += chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
png += chunk(b"IDAT", zlib.compress(rows, 9))
png += chunk(b"IEND", b"")
output = Path(__file__).resolve().parents[1] / "Assets" / "grain.png"
output.parent.mkdir(exist_ok=True)
output.write_bytes(png)
print(output)
