"""Builds the Ninth Inning's sprites: the kugibatto (a wooden bat with nails, its grip wrapped in white sarashi
cloth), recolored from Casey's nailed form (gun 616) frame by frame.

    python port/mods/Sukeban/tools/build_bat.py [--preview DIR]

Writes port/mods/Sukeban/Resources/kugibatto_atlas.png: the weapon atlas Casey's frames live in (512 x 1024), with
only the nailed bat's frames, recolored in place (rotated ones included), the rest transparent. NinthInning.cs gives
the gun a copy of that sprite collection whose materials use this texture, so every offset, attach point and
animation of Casey's carries over.
"""
import os
import sys
from PIL import Image

import tk2d

HERE = os.path.dirname(os.path.abspath(__file__))
MOD = os.path.dirname(HERE)
ROOT = os.path.abspath(os.path.join(MOD, "..", "..", ".."))
ASSETS = os.path.join(ROOT, "ripped", "ExportedProject", "Assets")
COLLECTION = os.path.join(ASSETS, "GameObject", "WeaponCollection3.prefab")
LIBRARY = os.path.join(ASSETS, "GameObject", "GunAnimation3.prefab")
ATLAS = os.path.join(ASSETS, "Texture2D", "atlas0_182.png")  # WeaponCollection3's texture
OUT = os.path.join(MOD, "Resources", "kugibatto_atlas.png")

CLIPS = ["baseball_bat_nail_fire", "baseball_bat_nail_charge", "baseball_bat_nail_reload"]
IDLE = "shotgunN_bat_idle_001"

# Casey's colors -> the kugibatto's
RECOLOR = {
    (255, 0, 0): (196, 138, 78),      # painted barrel -> bare wood
    (137, 0, 0): (132, 86, 46),       # barrel shade, swing smear -> wood shade
    (255, 216, 0): (222, 176, 112),   # end cap -> end grain
    (255, 150, 0): (170, 120, 66),    # end cap shade
    (255, 238, 143): (240, 208, 152), # end cap highlight
    (248, 166, 69): (236, 232, 222),  # wooden handle -> sarashi wrap
    (168, 113, 49): (184, 178, 166),  # handle shade -> wrap shade
    (174, 181, 185): (104, 68, 38),   # grip tape at the knob -> dark knob
    (131, 136, 139): (72, 46, 26),    # grip tape shade
    (95, 150, 211): (176, 182, 192),  # nails: steel
    (205, 223, 236): (234, 238, 242), # nail glint
    (54, 85, 121): (86, 90, 102),     # nail shadow
}


def nearest(rgb):
    return min(RECOLOR, key=lambda c: sum((a - b) ** 2 for a, b in zip(rgb, c)))


def main(argv):
    preview = argv[argv.index("--preview") + 1] if "--preview" in argv else None
    defs = tk2d.definitions(COLLECTION)
    clips = tk2d.clips(LIBRARY)
    atlas = Image.open(ATLAS).convert("RGBA")
    names = {IDLE} | {defs[i].name for c in CLIPS for i, _ in clips[c]}
    frames = [d for d in defs if d.name in names]
    out = Image.new("RGBA", atlas.size, (0, 0, 0, 0))
    for d in frames:
        x0, y0, x1, y1 = d.rect(*atlas.size)
        for y in range(y0, y1):
            for x in range(x0, x1):
                p = atlas.getpixel((x, y))
                if p[3]:
                    out.putpixel((x, y), RECOLOR[nearest(p[:3])] + (p[3],))
    out.save(OUT)
    print(f"{len(frames)} frames -> {os.path.relpath(OUT, ROOT)}")

    if preview:
        sc = 6
        crops = []
        for d in sorted(frames, key=lambda d: d.name):
            pair = []
            for src in (atlas, out):
                im = src.crop(d.rect(*atlas.size))
                pair.append(im.transpose(Image.Transpose.ROTATE_90) if d.flipped else im)
            crops.append(pair)
        w = sum(max(a.width, 10) * sc + 12 for a, _ in crops)
        h = max(a.height for a, _ in crops) * sc * 2 + 24
        img = Image.new("RGBA", (w, h), (58, 52, 50, 255))
        x = 0
        for a, b in crops:
            img.alpha_composite(a.resize((a.width * sc, a.height * sc), Image.NEAREST), (x, 0))
            img.alpha_composite(b.resize((b.width * sc, b.height * sc), Image.NEAREST), (x, h // 2))
            x += max(a.width, 10) * sc + 12
        img.save(os.path.join(preview, "kugibatto.png"))


if __name__ == "__main__":
    main(sys.argv[1:])
