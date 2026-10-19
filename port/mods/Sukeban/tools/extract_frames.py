"""Cuts the frames of a tk2d sprite collection exported by AssetRipper (Collection.prefab + atlas0.png) into PNGs.

Reference material for drawing a character on a base character's frames (sizes, poses, frame counts):
    python port/mods/Sukeban/tools/extract_frames.py "ripped/ExportedProject/Assets/sprites/characters/convict data" OUT [prefix...]
Writes OUT/<sprite name>.png for every definition (or those starting with one of the prefixes). Output is the
game's art: keep it out of git.
"""
import os
import re
import sys
from PIL import Image


def definitions(prefab):
    name, uvs, flipped = None, [], 0
    in_uvs = False
    with open(prefab, encoding="utf-8") as f:
        for line in f:
            m = re.match(r"  - name: (.*)$", line)
            if m:
                if name and len(uvs) == 4:
                    yield name, uvs, flipped
                name, uvs, flipped, in_uvs = m.group(1).strip(), [], 0, False
                continue
            if name is None:
                continue
            if line.startswith("    uvs:"):
                in_uvs = True
                continue
            if in_uvs:
                m = re.match(r"    - \{x: ([-0-9.e]+), y: ([-0-9.e]+)\}", line)
                if m:
                    uvs.append((float(m.group(1)), float(m.group(2))))
                    continue
                in_uvs = False
            m = re.match(r"    flipped: (\d)", line)
            if m:
                flipped = int(m.group(1))
    if name and len(uvs) == 4:
        yield name, uvs, flipped


def main(folder, out, prefixes):
    prefab = next(os.path.join(folder, n) for n in os.listdir(folder) if n.endswith(".prefab"))
    atlas = Image.open(os.path.join(folder, "atlas0.png")).convert("RGBA")
    w, h = atlas.size
    os.makedirs(out, exist_ok=True)
    n = 0
    for name, uvs, flipped in definitions(prefab):
        if prefixes and not any(name.startswith(p) for p in prefixes):
            continue
        xs = [u[0] for u in uvs]
        ys = [u[1] for u in uvs]
        box = (round(min(xs) * w), round((1 - max(ys)) * h), round(max(xs) * w), round((1 - min(ys)) * h))
        frame = atlas.crop(box)
        if flipped:
            # tk2d stores rotated sprites 90 degrees clockwise
            frame = frame.transpose(Image.Transpose.ROTATE_90)
        frame.save(os.path.join(out, name + ".png"))
        n += 1
    print(f"{n} frames -> {out}")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2], sys.argv[3:])
