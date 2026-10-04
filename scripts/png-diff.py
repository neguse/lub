#!/usr/bin/env python3
"""Compare two PNG captures within a tolerance.

Usage: scripts/png-diff.py <a.png> <b.png> [--mean 3.0] [--outliers 1.0]

A real GPU does not reproduce a software rasterizer byte for byte, so the
macOS golden check (scripts/run-golden.sh) compares against the Linux
goldens with this instead of cmp. A pixel's difference is the largest
absolute difference over R, G and B. The images match when the mean
difference is at most --mean and at most --outliers percent of the pixels
differ by more than 16.

Only what stb_image_write produces is read: 8-bit, non-interlaced PNG.
"""
import struct
import sys
import zlib


def load(path):
    data = open(path, "rb").read()
    pos, idat = 8, b""
    width = height = channels = 0
    while pos < len(data):
        length, kind = struct.unpack(">I4s", data[pos : pos + 8])
        body = data[pos + 8 : pos + 8 + length]
        pos += 12 + length
        if kind == b"IHDR":
            width, height, depth, color, _, _, interlace = struct.unpack(">IIBBBBB", body)
            if depth != 8 or interlace != 0 or color not in (0, 2, 4, 6):
                raise SystemExit(f"{path}: unsupported PNG (depth {depth}, color {color})")
            channels = {0: 1, 2: 3, 4: 2, 6: 4}[color]
        elif kind == b"IDAT":
            idat += body
    raw = zlib.decompress(idat)
    stride = width * channels
    rows, prev, pos = [], bytearray(stride), 0
    for _ in range(height):
        kind = raw[pos]
        line = bytearray(raw[pos + 1 : pos + 1 + stride])
        pos += 1 + stride
        if kind == 1:
            for i in range(channels, stride):
                line[i] = (line[i] + line[i - channels]) & 255
        elif kind == 2:
            for i in range(stride):
                line[i] = (line[i] + prev[i]) & 255
        elif kind == 3:
            for i in range(stride):
                left = line[i - channels] if i >= channels else 0
                line[i] = (line[i] + (left + prev[i]) // 2) & 255
        elif kind == 4:
            for i in range(stride):
                a = line[i - channels] if i >= channels else 0
                b = prev[i]
                c = prev[i - channels] if i >= channels else 0
                pa, pb, pc = abs(b - c), abs(a - c), abs(a + b - 2 * c)
                line[i] = (line[i] + (a if pa <= pb and pa <= pc else b if pb <= pc else c)) & 255
        rows.append(line)
        prev = line
    return width, height, channels, rows


def rgb(rows, channels):
    # gray (+alpha) は R = G = B として扱う
    planes = [0, 0, 0] if channels < 3 else [0, 1, 2]
    return [[row[p::channels] for p in planes] for row in rows]


def main():
    args, mean_limit, outlier_limit = [], 3.0, 1.0
    argv = sys.argv[1:]
    while argv:
        arg = argv.pop(0)
        if arg == "--mean":
            mean_limit = float(argv.pop(0))
        elif arg == "--outliers":
            outlier_limit = float(argv.pop(0))
        else:
            args.append(arg)
    if len(args) != 2:
        raise SystemExit(__doc__)
    aw, ah, ac, arows = load(args[0])
    bw, bh, bc, brows = load(args[1])
    if (aw, ah) != (bw, bh):
        print(f"size {aw}x{ah} != {bw}x{bh}")
        return 1
    total = worst = outliers = 0
    for (ar, ag, ab), (br, bg, bb) in zip(rgb(arows, ac), rgb(brows, bc)):
        for r0, g0, b0, r1, g1, b1 in zip(ar, ag, ab, br, bg, bb):
            d = max(abs(r0 - r1), abs(g0 - g1), abs(b0 - b1))
            total += d
            if d > worst:
                worst = d
            if d > 16:
                outliers += 1
    pixels = aw * ah
    mean, percent = total / pixels, 100.0 * outliers / pixels
    ok = mean <= mean_limit and percent <= outlier_limit
    print(f"mean={mean:.3f} max={worst} over16={percent:.2f}% ({'ok' if ok else 'differs'})")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
