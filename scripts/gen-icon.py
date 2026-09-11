#!/usr/bin/env python3
"""Genera Resources/alarmino.ico con un icono de campana dibujado por código.

Sin dependencias externas: escribe un ICO clásico (BGRA + máscara AND).
"""
import os
import struct
import sys

SIZES = [16, 24, 32, 48, 64, 128, 256]

CARD = (30, 32, 38, 255)
BELL = (250, 178, 71, 255)
BELL_DARK = (198, 132, 32, 255)
CLAPPER = (255, 220, 150, 255)
HIGH = (214, 228, 255, 255)


def in_rounded(px, py, n, margin, radius):
    """Punto central (px,py) en [0,n] dentro de un cuadrado redondeado."""
    lo = margin
    hi = n - margin
    if not (lo <= px <= hi and lo <= py <= hi):
        return False
    if px >= lo + radius and px <= hi - radius:
        return True
    if py >= lo + radius and py <= hi - radius:
        return True
    cx = lo + radius if px < lo + radius else hi - radius
    cy = lo + radius if py < lo + radius else hi - radius
    return (px - cx) ** 2 + (py - cy) ** 2 <= radius * radius


def sample(n, px, py):
    """Devuelve RGBA para el punto central (px,py) en un icono de tamaño n."""
    margin = max(0.5, n * 0.03)
    radius = max(1.0, n * 0.16)
    if not in_rounded(px, py, n, margin, radius):
        return (0, 0, 0, 0)

    fx = px / n
    fy = py / n

    color = CARD
    if not in_rounded(px, py, n, margin + 1.0, radius + 1.0):
        color = BELL_DARK

    cx = 0.50 * n
    dome_r = 0.30 * n
    base_y = 0.90 * n

    dx = px - cx
    dy = py - base_y

    # Cuerpo de la campana: semicírculo superior
    if dy < 0 and dx * dx + dy * dy <= dome_r * dome_r:
        color = BELL
        # reflejo
        if dx > -dome_r * 0.05 and dy < -dome_r * 0.35:
            color = HIGH
        elif dy < -dome_r * 0.6 or dx < -dome_r * 0.7:
            color = BELL_DARK
    # Lados convergentes hacia arriba (base redondeada)
    wall_l = dome_r * 0.62
    wall_r = dome_r * 0.62
    if -dome_r * 0.45 <= dy <= 0:
        t = 1 - (dy / (-dome_r * 0.45))  # 0 abajo, 1 arriba
        half = wall_l * (0.72 + 0.28 ** (1 - t))
        if half <= abs(dx) <= wall_l - (wall_l - half) * t:
            color = BELL
            if dx < 0:
                color = BELL_DARK
    # Borde de salida (base ancha)
    if 0 <= dy <= dome_r * 0.22 and abs(dx) <= wall_l + dome_r * 0.04:
        color = BELL
    if dome_r * 0.22 < dy <= dome_r * 0.34 and abs(dx) <= wall_l * 0.9:
        color = BELL_DARK
    # Badajo
    if abs(dx) <= dome_r * 0.15:
        if -dome_r * 0.42 <= dy <= dome_r * 0.30:
            color = CLAPPER

    return color


def render_icon(n):
    bgra = bytearray()
    for y in range(n):
        line = bytearray()
        for x in range(n):
            # supersampling 2x2 del centro de píxel (x+0.5, y+0.5)
            acc = [0, 0, 0, 0]
            for sy in (0.25, 0.75):
                for sx in (0.25, 0.75):
                    r, g, b, a = sample(n, x + sx, y + sy)
                    acc[0] += r; acc[1] += g; acc[2] += b; acc[3] += a
            r, g, b, a = (c // 4 for c in acc)
            line += bytes((b, g, r, a))
        line = bytes(line)
        pad = (4 - (len(line) % 4)) % 4
        line += b"\x00" * pad
        bgra += line
    return bytes(bgra)


def main(out_path):
    out = bytearray(struct.pack("<HHH", 0, 1, len(SIZES)))
    blobs = {}
    offset = 6 + 16 * len(SIZES)
    for sz in SIZES:
        bgra = render_icon(sz)
        and_row = b"\x00" * sz
        mask = (and_row + b"\x00" * ((4 - (sz % 4)) % 4)) * sz
        img_size = 40 + len(bgra) + len(mask)
        dim = 0 if sz == 256 else sz
        out += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, img_size, offset)
        blobs[sz] = (bgra, mask)
        offset += img_size
    for sz in SIZES:
        bgra, mask = blobs[sz]
        header = struct.pack(
            "<IiiHHIIiiII",
            40, sz, sz * 2, 1, 32, 0, len(bgra), 0, 0, 0, 0,
        )
        out += header
        out += bgra
        out += mask

    os.makedirs(os.path.dirname(out_path), exist_ok=True)
    with open(out_path, "wb") as f:
        f.write(out)
    print(f"OK {out_path} ({len(out)} bytes, {len(SIZES)} tamaños)")


if __name__ == "__main__":
    target = sys.argv[1] if len(sys.argv) > 1 else "src/Alarmino/Resources/alarmino.ico"
    main(target)