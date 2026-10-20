"""Placeholder pixel art for the Sukeban mod, drawn from the character maps below.

Writes the PNGs the mod embeds (Resources/ and Character/loadoutsprites/). Run again after editing a map:
    python port/mods/Sukeban/tools/placeholder_sprites.py
Real sprites will replace these; the file names are what the code loads.
"""
import os
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
MOD = os.path.dirname(HERE)

PALETTE = {
    ".": None,
    "k": (24, 20, 28),       # outline
    "B": (196, 170, 120),    # canvas
    "L": (226, 206, 160),    # canvas highlight
    "D": (150, 122, 80),     # canvas shade
    "R": (196, 40, 48),      # red cord
    "T": (120, 92, 60),      # tied neck
    "d": (110, 74, 46),      # dirt
    "e": (84, 54, 34),       # dirt shade
    "f": (138, 98, 62),      # dirt light
    "S": (150, 158, 172),    # steel
    "s": (196, 204, 216),    # steel highlight
    "G": (100, 106, 122),    # steel shade
    "r": (232, 232, 236),    # rivet
    "N": (34, 46, 92),       # navy strap
    "n": (60, 76, 140),      # navy highlight
    "W": (244, 244, 240),    # home plate
    "w": (200, 200, 196),    # home plate shade
    "O": (150, 96, 54),      # bat wood
    "o": (196, 136, 80),     # bat wood light
    "g": (180, 184, 192),    # nail
    "C": (208, 46, 54),      # ball stitches
    "x": (0, 0, 0, 96),      # shadow (translucent)
    "Y": (90, 60, 36),       # geta wood shade
    "y": (150, 104, 64),     # geta wood
}

ELBOW_GUARD = """
.....kkkkkk.....
...kksssSSSkk...
..ksssSSSSSSGk..
.kssSSSSSSSSSGk.
.ksSSrSSSSSrSGk.
kSSSSSSSSSSSSSGk
kNNNNNNNNNNNNNNk
knnnnnnnnnnnnnnk
kNNNNNNNNNNNNNNk
kSSSSSSSSSSSSSGk
.kSSSrSSSSSrSGk.
.kGSSSSSSSSSGGk.
..kGGSSSSSSGGk..
...kkGGGGGGkk...
.....kkkkkk.....
"""

NINTH_INNING_ICON = """
............kk..
...........kook.
..........kgOOk.
.........kooOkg.
........koOOk...
.......kgOOk....
......kooOkg....
.....kOOOk......
....kgOOk.......
...koOOk........
..kOOOk.........
.kNNNk..........
kNnNk...........
kNNk............
.kk.............
"""

# Fungo: the ball she tosses up for herself (also the hit ball's projectile sprite) and its shadow on the floor.
FUNGO_BALL = """
.kkkk.
kWWWCk
kWWCWk
kCWWWk
kwWWwk
.kkkk.
"""

FUNGO_SHADOW = """
.xxxx.
xxxxxx
.xxxx.
"""

FUNGO_ICON = """
................
.....kkkkkk.....
...kkWWWWWWkk...
..kWCWWWWWWCWk..
..kWCWWWWWWCWk..
.kWWWCWWWWCWWWk.
.kWWWCWWWWCWWWk.
.kWWWCWWWWCWWWk.
.kWWWCWWWWCWWWk.
..kWCWWWWWWCWk..
..kwCwWWWWwCwk..
...kkwwwwwwkk...
.....kkkkkk.....
................
"""

# Geta: a wooden sandal seen from the side, red thong.
GETA_ICON = """
................
................
................
.......kk.......
......kCCk......
.....kC..Ck.....
....kC....Ck....
.kkkkkkkkkkkkkk.
kyyyyyyyyyyyyyyk
kYYYYYYYYYYYYYYk
.kkkkkkkkkkkkkk.
..kyyk....kyyk..
..kYYk....kYYk..
..kkkk....kkkk..
................
"""

def draw(art, path):
    rows = [r for r in art.strip("\n").split("\n")]
    w = max(len(r) for r in rows)
    img = Image.new("RGBA", (w, len(rows)), (0, 0, 0, 0))
    for y, row in enumerate(rows):
        for x, c in enumerate(row.ljust(w, ".")):
            color = PALETTE[c]
            if color:
                img.putpixel((x, y), color if len(color) == 4 else color + (255,))
    os.makedirs(os.path.dirname(path), exist_ok=True)
    img.save(path)
    print(f"  {os.path.relpath(path, MOD)} {img.size[0]}x{img.size[1]}")


def main():
    res = os.path.join(MOD, "Resources")
    loadout = os.path.join(MOD, "Character", "loadoutsprites")
    draw(ELBOW_GUARD, os.path.join(res, "elbow_guard.png"))
    draw(FUNGO_ICON, os.path.join(res, "fungo.png"))
    draw(FUNGO_BALL, os.path.join(res, "fungo_ball.png"))
    draw(FUNGO_SHADOW, os.path.join(res, "fungo_shadow.png"))
    draw(GETA_ICON, os.path.join(res, "geta.png"))
    draw(NINTH_INNING_ICON, os.path.join(loadout, "loadout_ninth_inning.png"))
    draw(FUNGO_ICON, os.path.join(loadout, "loadout_fungo.png"))
    draw(ELBOW_GUARD, os.path.join(loadout, "loadout_elbow_guard.png"))
    draw(GETA_ICON, os.path.join(loadout, "loadout_geta.png"))


if __name__ == "__main__":
    main()
