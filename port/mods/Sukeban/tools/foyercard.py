"""Builds Sukeban's Breach card (the one over a character's head when you walk up in the Breach) from her portrait.

    python port/mods/Sukeban/tools/foyercard.py [--preview DIR]

The game's cards are the 34 x 34 portrait in a black-and-white frame (38 x 38). They appear with a flip: edge, half,
front, half, edge, half back, back, half back, edge, half, front - 11 frames whose sizes Alexandria takes from the
Gunslinger's card (so ours must have the same: 8, 27 or 38 wide). The edges and the back are the game's own (the
same on every card); the front is Character/facecard.png framed, squeezed for the halves. The idle loop (4 frames)
runs a glint along the sheen of her hair.

Writes port/mods/Sukeban/Character/foyercard/sukeban_facecard_{appear,idle}_NNN.png (Alexandria: names with
"appear" / "idle" in that folder).
"""
import os
import sys
from PIL import Image

import tk2d

HERE = os.path.dirname(os.path.abspath(__file__))
MOD = os.path.dirname(HERE)
ROOT = os.path.abspath(os.path.join(MOD, "..", "..", ".."))
CARDS = os.path.join(ROOT, "ripped", "ExportedProject", "Assets", "sprites", "ui", "character select")
COLLECTION = os.path.join(CARDS, "facecard_collection data", "Facecard_Collection.prefab")
ATLAS = os.path.join(CARDS, "facecard_collection data", "atlas0.png")
PORTRAIT = os.path.join(MOD, "Character", "facecard.png")
OUT = os.path.join(MOD, "Character", "foyercard")

BLACK, WHITE = (0, 0, 0, 255), (255, 255, 255, 255)
GROUND = (14, 12, 22, 255)  # behind her head
SHEEN, GLINT = (98, 112, 164), (150, 166, 214)

# the flip: what each of the 11 appear frames shows, Convict's card for the game's own parts
FLIP = ["edge:convict_facecard_appear_001", "half", "front", "half", "edge:convict_facecard_appear_005",
        "back:convict_facecard_appear_006", "back:convict_facecard_appear_007", "back:convict_facecard_appear_008",
        "edge:convict_facecard_appear_009", "half", "front"]


def framed(portrait):
    """38 x 38: a black outer line, a white inner one, the portrait inside on a dark ground (the other cards')."""
    w, h = portrait.size
    card = Image.new("RGBA", (w + 4, h + 4), BLACK)
    for x in range(1, w + 3):
        for y in range(1, h + 3):
            inner = 2 <= x < w + 2 and 2 <= y < h + 2
            card.putpixel((x, y), GROUND if inner else WHITE)
    card.alpha_composite(portrait, (2, 2))
    return card


def squeezed(portrait, width):
    """The card turned halfway: the portrait narrowed, the frame kept."""
    inner = portrait.resize((width - 4, portrait.height), Image.NEAREST)
    return framed(inner)


def glint_frames(portrait, steps=4):
    """The idle loop: the glint walks down the sheen line, one pixel a frame."""
    px = portrait.load()
    sheen = [(x, y) for y in range(portrait.height) for x in range(portrait.width)
             if px[x, y][3] and px[x, y][:3] in (SHEEN, GLINT)]
    sheen.sort(key=lambda p: (p[1], -p[0]))  # top-right first: along the stroke
    frames = []
    for k in range(steps):
        im = portrait.copy()
        q = im.load()
        for x, y in sheen:
            q[x, y] = SHEEN + (255,)
        # two glint pixels a third of the stroke apart, moving along it
        for off in (0, len(sheen) // 3):
            i = (k * max(1, len(sheen) // (steps * 2)) + off) % len(sheen)
            x, y = sheen[i]
            q[x, y] = GLINT + (255,)
        frames.append(framed(im))
    return frames


def main(argv):
    preview = argv[argv.index("--preview") + 1] if "--preview" in argv else None
    defs = {d.name: d for d in tk2d.definitions(COLLECTION)}
    atlas = Image.open(ATLAS).convert("RGBA")
    portrait = Image.open(PORTRAIT).convert("RGBA")

    def game_frame(name):
        d = defs[name]
        im = atlas.crop(d.rect(*atlas.size))
        return im.transpose(Image.Transpose.ROTATE_90) if d.flipped else im

    front = framed(portrait)
    appear = []
    for what in FLIP:
        if what == "front":
            appear.append(front)
        elif what == "half":
            appear.append(squeezed(portrait, 27))
        else:
            appear.append(game_frame(what.split(":")[1]))
    idle = glint_frames(portrait)
    sizes = [im.size for im in appear]
    assert [w for w, _ in sizes] == [8, 27, 38, 27, 8, 27, 38, 27, 8, 27, 38], sizes  # the Gunslinger's

    os.makedirs(OUT, exist_ok=True)
    for k, im in enumerate(appear):
        im.save(os.path.join(OUT, f"sukeban_facecard_appear_{k + 1:03d}.png"))
    for k, im in enumerate(idle):
        im.save(os.path.join(OUT, f"sukeban_facecard_idle_{k + 1:03d}.png"))
    print(f"{len(appear)} appear + {len(idle)} idle frames -> {os.path.relpath(OUT, ROOT)}")

    if preview:
        sc = 5
        frames = appear + idle
        img = Image.new("RGBA", (len(frames) * 42 * sc, 42 * sc), (58, 52, 50, 255))
        for k, im in enumerate(frames):
            img.alpha_composite(im.resize((im.width * sc, im.height * sc), Image.NEAREST),
                                (k * 42 * sc + (38 - im.width) * sc // 2, 0))
        img.save(os.path.join(preview, "foyercard.png"))


if __name__ == "__main__":
    main(sys.argv[1:])
