#!/usr/bin/env python3
"""Extract the Halloween skin from the Windows build of Evertried into a .dxskin file for DxSkin.

The Linux depot is an older build without the Halloween skin. The Windows depot has it, and its
animations are frame-for-frame copies of the Killer skin's (Player_Killer_N -> Player_Halloween_N),
so DxSkin plays Killer's animations and swaps each sprite for the Halloween one built from this file.

    uv run --with UnityPy tools/extract_skin.py <Windows Evertried_Data dir> <out.dxskin>

The output contains game art and must stay out of git (*.dxskin is ignored).

Format (little-endian): "DXSKIN1\\0", key, displayName (int32 length + UTF-8), int32 width, height,
int32 spriteCount, then per sprite int32 index + float32 x, y, w, h, pivotX, pivotY, ppu; then the
scarf gradient and the One With All gradient (int32 mode, int32 colorKeys, (r, g, b, time) each,
int32 alphaKeys, (a, time) each); then width*height*4 bytes of RGBA32 texture data, rows bottom-up.
"""
import re
import struct
import sys

import UnityPy

SKIN = "PlayerSkin_Halloween"
SHEET = "Player_Halloween"
SCARF = "Halloween_ScarfEffect_VFX"


def rstr(b, o):
    n = struct.unpack_from("<i", b, o)[0]
    s = b[o + 4:o + 4 + n].decode("utf-8")
    return s, (o + 4 + n + 3) & ~3


def gradient_from_raw(b, o):
    """Unity 2019.4 Gradient: key0..7 (RGBA floats), ctime0..7, atime0..7 (uint16), mode, counts."""
    keys = [struct.unpack_from("<4f", b, o + 16 * i) for i in range(8)]
    o += 128
    ctime = struct.unpack_from("<8H", b, o)
    atime = struct.unpack_from("<8H", b, o + 16)
    o += 32
    mode, ncol, nalpha = struct.unpack_from("<iBB", b, o)
    return gradient(keys, ctime, atime, mode, ncol, nalpha)


def gradient_from_tree(g):
    keys = [(g[f"key{i}"]["r"], g[f"key{i}"]["g"], g[f"key{i}"]["b"], g[f"key{i}"]["a"]) for i in range(8)]
    ctime = [g[f"ctime{i}"] for i in range(8)]
    atime = [g[f"atime{i}"] for i in range(8)]
    return gradient(keys, ctime, atime, g["m_Mode"], g["m_NumColorKeys"], g["m_NumAlphaKeys"])


def gradient(keys, ctime, atime, mode, ncol, nalpha):
    color = [(keys[i][0], keys[i][1], keys[i][2], ctime[i] / 65535.0) for i in range(ncol)]
    alpha = [(keys[i][3], atime[i] / 65535.0) for i in range(nalpha)]
    return mode, color, alpha


def pack_gradient(g):
    mode, color, alpha = g
    out = struct.pack("<ii", mode, len(color))
    out += b"".join(struct.pack("<4f", *c) for c in color)
    out += struct.pack("<i", len(alpha))
    out += b"".join(struct.pack("<2f", *a) for a in alpha)
    return out


def pack_str(s):
    b = s.encode("utf-8")
    return struct.pack("<i", len(b)) + b


def main():
    data_dir, out_path = sys.argv[1], sys.argv[2]
    env = UnityPy.load(data_dir)

    skin = texture = scarf = None
    sprites = {}
    for obj in env.objects:
        t = obj.type.name
        if t == "Sprite":
            d = obj.read()
            m = re.fullmatch(SHEET + r"_(\d+)", d.m_Name)
            if m:
                sprites[int(m.group(1))] = d
        elif t == "Texture2D" and texture is None:
            d = obj.read()
            if d.m_Name == SHEET:
                texture = d
        elif t == "MonoBehaviour" and skin is None:
            raw = obj.get_raw_data()
            try:
                name, o = rstr(raw, 28)
            except Exception:
                continue
            if name == SKIN:
                skin = (raw, o)
        elif t == "GameObject" and scarf is None:
            d = obj.read()
            if d.m_Name == SCARF:
                for c in d.m_Components:
                    p = c.component if hasattr(c, "component") else c
                    try:
                        r = p.read()  # the scarf's MonoBehaviours have no type tree; only the ParticleSystem matters
                    except ValueError:
                        continue
                    if r.object_reader.type.name == "ParticleSystem":
                        scarf = r.object_reader.read_typetree()

    missing = [n for n, v in (("skin", skin), ("texture", texture), ("scarf", scarf), ("sprites", sprites)) if not v]
    if missing:
        sys.exit("not found in " + data_dir + ": " + ", ".join(missing) + " (is this the Windows build?)")

    raw, o = skin
    key, o = rstr(raw, o)
    display, o = rstr(raw, o)
    o += 12  # displayNameLocalized PPtr
    animation_id = struct.unpack_from("<i", raw, o)[0]
    o += 4 + 12  # animationID, scarfObject PPtr
    one_with_all = gradient_from_raw(raw, o)
    scarf_gradient = gradient_from_tree(scarf["ColorModule"]["gradient"]["maxGradient"])

    if texture.m_TextureFormat != 4:  # RGBA32
        sys.exit(f"unexpected texture format {texture.m_TextureFormat}")
    w, h = texture.m_Width, texture.m_Height
    pixels = bytes(texture.get_image_data())  # raw RGBA32 as Unity stores it (rows bottom-up), from the .resS stream
    if len(pixels) != w * h * 4:
        sys.exit("texture size mismatch")

    out = bytearray(b"DXSKIN1\0")
    out += pack_str(key) + pack_str(display)
    out += struct.pack("<iii", w, h, len(sprites))
    for i in sorted(sprites):
        d = sprites[i]
        r = d.m_Rect
        out += struct.pack("<i7f", i, r.x, r.y, r.width, r.height, d.m_Pivot.x, d.m_Pivot.y, d.m_PixelsToUnits)
    out += pack_gradient(scarf_gradient) + pack_gradient(one_with_all)
    out += pixels
    with open(out_path, "wb") as f:
        f.write(out)
    print(f"{out_path}: key={key!r} name={display!r} (Windows animation id {animation_id}), "
          f"{len(sprites)} sprites, {w}x{h}, scarf {len(scarf_gradient[1])} color keys, "
          f"One With All {len(one_with_all[1])} color keys, {len(out)} bytes")


if __name__ == "__main__":
    main()
