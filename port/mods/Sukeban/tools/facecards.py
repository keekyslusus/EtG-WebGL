"""Sukeban's dialogue portrait (facecard) variants: 34 x 34 like the game's own (Convict's is
talking_bar_character_window_convict_003 in GameUIAtlas). One drawn base; the variants change eyes, mouth, props.

    python port/mods/Sukeban/tools/facecards.py OUTDIR [--write]

Writes OUTDIR/facecard_<variant>.png and facecards_preview.png. --write puts the chosen one (CHOSEN) into
Character/facecard.png, which Alexandria adds to the dialogue portraits' atlas as Player<name>_facecard. From then
on that PNG is the master (hand edits welcome): without --write this script leaves it alone.
"""
import os
import sys
from PIL import Image

PAL = {
    "K": (20, 18, 30),       # darkest line
    "H": (30, 28, 46),       # hair edge
    "h": (46, 48, 80),       # hair
    "m": (64, 70, 112),      # hair mid
    "l": (98, 112, 164),     # sheen
    "L": (150, 166, 214),    # sheen glint
    "S": (246, 208, 172),    # skin light
    "f": (236, 190, 152),    # skin in the bangs' shadow
    "s": (222, 172, 132),    # skin
    "d": (180, 126, 94),     # skin shade
    "D": (124, 82, 64),      # deep shade
    "W": (242, 242, 248),    # eye white / collar stripe
    "I": (52, 46, 76),       # iris
    "M": (204, 104, 108),    # lips
    "P": (240, 168, 150),    # blush
    "N": (62, 72, 120),      # sailor top
    "n": (40, 46, 82),       # top shade
    "R": (214, 50, 58),      # scarf
    "r": (150, 28, 40),      # scarf shade
    "T": (204, 150, 92),     # wood
    "t": (136, 92, 52),      # wood shade
    "J": (120, 168, 72),     # grass stalk
    "j": (78, 116, 50),      # grass stalk shade
    "G": (196, 202, 212),    # nail
    "g": (110, 114, 126),    # nail shade
}

# A narrow face tapering to a pointed chin, framed by the locks; blush under the eyes, a slim neck. The hair
# (rows 0-12) is the bob of her sprites. Eyes and mouth come from the variants.
BASE = """
..........HHHHHHHHHHHHHH..........
.......HHHhhhhhhhhhhhhhhHHH.......
.....HHhhhhhhhhhhhhllhhhhhhHH.....
....Hhhhhhmhhhhhhhllhhhhmhhhhh....
...HhhhhhmhhhhhhhllhhhhhhmhhhhH...
..HhhhhhmhhhhhhllLhhhhhhhhmhhhhH..
..HhhhhhmhhhhhllLhhhhhhhhhmhhhhH..
.HhhhhhmhhhhhllhhhhhhhhhhhhmhhhhH.
.HhhhhmhhhhhhlhhhHhhhhhHhhhhmhhhH.
.HhhhhmhhhHhhhhhhHhhhhhHhhhhmhhhH.
.HhhhmhhhHhhhHhhhHhhhhhHhhHhhmhhH.
.HhhhmhhHhshhHhshHhhshhHhsHhhmhhH.
.HhhhmhhHsshhHshhHhhhshHhssHhmhhH.
HhhhmhhhHfhhfffhffffhffhfHhmhhhhhH
HhhhmhhhHfffSSSfSSSSfSSffHhmhhhhhH
HhhmhhhhHSSSSSSSSSSSSSSSsHhhhhmhhH
HhhmhhhhHSSSSSSSSSSSSSSSsHhhhhmhhH
HhhmhhhhHSSSSSSSSSSSSSSSsHhhhhmhhH
HhhmhhhhHSSSSSSSSSSSSSSSsHhhhhmhhH
HhhmhhhhHSSSSSSSSSSSSSSSsHhhhhmhhH
.HhmhhhhHSPPSSSSSSSSSPPsHhhhhmhH..
.HhmhhhhhHSSSSSSSdSSSSssHhhhhmhH..
.HhmhhhhhHSSSSSSSSSSSSssHhhhhmhH..
..HmhhhhhhHSSSSSSSSSSssHhhhhmhH...
..HmhhhhhhhHdSSSSSSssdHhhhhhmhH...
...HhhhhhhhhHdSSSSssdHhhhhhhhH....
...HhH.HhhhhhHdSSSsdHhhhhhH.HhH...
...H...H.HhhhhHDddDHhhhhH.H...H...
..............DddddD..............
.......NNNNNNnDddddDnNNNNNNNNN....
.....NNNNNNNNnnRRRRRRrnnNNNNNNNN..
...NNWNNNNNNNNNnRRRRrnNNNNNNNNWNN.
..NNNNWWNNNNNNNNnRRrnNNNNNNNWWNNNN
.NNNNNNNWWNNNNNNNnrnNNNNNNWWNNNNNN
"""

# Big eyes with lashes flicking out at the outer corners: left at x 10-14, right at x 19-23.
EYES = {
    # 5 x 3 with the lash line above: pupil and glint up top, the iris rounding off below
    "open": [
        (14, 9, "K"), (14, 24, "K"), (15, 9, "KKKKKK"), (15, 19, "KKKKKK"),
        (16, 10, "WIKWW"), (16, 19, "WIKWW"), (17, 10, "WIIIW"), (17, 19, "WIIIW"),
        (18, 10, "dWIWd"), (18, 19, "dWIWd"),
    ],
    # lids slanting down toward the nose (the brows are under the bangs: drawn, they read as smudges)
    "glare": [
        (15, 9, "KKK"), (16, 11, "KKKK"), (15, 22, "KKK"), (16, 19, "KKKK"),
        (16, 10, "W"), (16, 23, "W"), (17, 10, "WIKIW"), (17, 19, "WIKIW"),
        (18, 10, "dWIWd"), (18, 19, "dWIWd"),
    ],
    # half-lidded
    "lazy": [
        (15, 9, "K"), (15, 24, "K"), (16, 9, "KKKKKK"), (16, 19, "KKKKKK"),
        (17, 10, "WIKIW"), (17, 19, "WIKIW"), (18, 10, "dWIWd"), (18, 19, "dWIWd"),
    ],
}

MOUTHS = {
    # small: a crooked smile, the right corner up
    "smirk": [(23, 15, "dMM"), (22, 18, "d")],
    "flat": [(23, 15, "dddd")],
    # a grass stalk in the corner of the smile (green: a brown stick across the cheek reads as a scar)
    "stalk": [(23, 15, "dMM"), (22, 18, "d"), (23, 18, "J"), (24, 19, "J"), (24, 20, "j"), (25, 21, "J"),
              (25, 22, "j"), (26, 23, "J")],
}

# The kugibatto over her right shoulder, behind her head: its barrel fills the top-right corner, nails sticking out.
BAT_ART = """
.........................GTTTTTtG.
...........................TTTTTtG
............................GTTTtg
..............................TTtG
...............................Tt.
................................T.
"""
BAT = [(y, x, c) for y, row in enumerate(BAT_ART.strip("\n").split("\n")) for x, c in enumerate(row) if c != "."]

VARIANTS = {
    "A_smirk": dict(eyes="open", mouth="smirk", bat=False),
    "B_glare": dict(eyes="glare", mouth="flat", bat=True),
    "C_stalk": dict(eyes="lazy", mouth="stalk", bat=False),
}


def draw(spec):
    rows = [list(r) for r in BASE.strip("\n").split("\n")]
    assert len(rows) == 34 and all(len(r) == 34 for r in rows), [len(r) for r in rows]
    if spec["bat"]:
        for y, x, c in BAT:
            if 0 <= x < 34 and 0 <= y < 34 and rows[y][x] == ".":
                rows[y][x] = c
    for y, x, s in EYES[spec["eyes"]] + MOUTHS[spec["mouth"]]:
        for i, c in enumerate(s):
            rows[y][x + i] = c
    im = Image.new("RGBA", (34, 34), (0, 0, 0, 0))
    for y, row in enumerate(rows):
        for x, c in enumerate(row):
            if c != ".":
                im.putpixel((x, y), PAL[c] + (255,))
    return im


# the one in the game (Character/facecard.png)
CHOSEN = "B_glare"


def main(out, write):
    if write:
        draw(VARIANTS[CHOSEN]).save(os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "Character", "facecard.png"))
    os.makedirs(out, exist_ok=True)
    images = {}
    for name, spec in VARIANTS.items():
        images[name] = draw(spec)
        images[name].save(os.path.join(out, f"facecard_{name}.png"))
    sc = 8
    prev = Image.new("RGBA", (len(images) * (34 * sc + 24), 34 * sc + 24 + 34 * 3), (58, 52, 50, 255))
    for k, im in enumerate(images.values()):
        prev.alpha_composite(im.resize((34 * sc, 34 * sc), Image.NEAREST), (k * (34 * sc + 24), 0))
        prev.alpha_composite(im.resize((34 * 3, 34 * 3), Image.NEAREST), (k * (34 * sc + 24), 34 * sc + 12))
    prev.save(os.path.join(out, "facecards_preview.png"))


if __name__ == "__main__":
    main(sys.argv[1], "--write" in sys.argv)
