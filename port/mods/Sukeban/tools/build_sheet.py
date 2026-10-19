"""Builds Sukeban's player sheet: Convict's atlas (same layout, so Alexandria's `playersheet` keeps every UV and
offset) with each frame turned into her.

    python port/mods/Sukeban/tools/build_sheet.py [--preview DIR]

Every frame is recolored (hair, uniform, skin). The frames of the key animations (idle, run in four directions,
with their hand variants) are rebuilt: Convict's head is cut out and one of four drawn heads (front, three-quarter
front, back, three-quarter back) is put where his was, found by his eyes (front views) or the bottom of his hair
(back views), so his bob and lean carry over; the jumpsuit becomes a cropped sailor top with a red scarf or the
collar's flap, a belt higher up and a long skirt filling the space between the legs, the feet geta. Dodge frames
and the rest are recolored only for now.

Writes port/mods/Sukeban/Character/playersheet.png (embedded by the mod; Alexandria loads it by that name).
"""
import os
import sys
from PIL import Image

import tk2d

HERE = os.path.dirname(os.path.abspath(__file__))
MOD = os.path.dirname(HERE)
ROOT = os.path.abspath(os.path.join(MOD, "..", "..", ".."))
ASSETS = os.path.join(ROOT, "ripped", "ExportedProject", "Assets")
COLLECTION = os.path.join(ASSETS, "sprites", "characters", "convict data")
LIBRARY = os.path.join(ASSETS, "sprites", "characters", "animations", "ConvictAnimation.prefab")
OUT = os.path.join(MOD, "Character", "playersheet.png")

# Convict's colors
C_HAIR, C_HAIR_SHADE, C_DARK = (241, 210, 0), (211, 129, 0), (159, 79, 0)
C_SHINE, C_HAIR_DARK2 = (255, 255, 255), (137, 64, 0)
C_SUIT, C_SUIT_DARK, C_MARK = (217, 66, 0), (115, 35, 0), (44, 45, 50)
C_SKIN, C_SKIN_SHADE, C_EYE, C_BLACK = (218, 163, 109), (186, 137, 90), (0, 62, 205), (0, 0, 0)
HAIR_COLORS = {C_HAIR, C_HAIR_SHADE, C_DARK, C_SHINE, C_HAIR_DARK2}
# Only these say "head": the dark brown also outlines his hands.
HAIR_ONLY = {C_HAIR, C_HAIR_SHADE, C_SHINE}
SUIT_COLORS = {C_SUIT, C_SUIT_DARK, C_MARK}
SKIN_COLORS = {C_SKIN, C_SKIN_SHADE}
SKIN_OUTLINE = (168, 112, 80)

# Sukeban's colors (design v4, the same letters as the head drawings below)
PAL = {
    "H": (26, 24, 40),      # hair, dark edge
    "h": (44, 46, 76),      # hair
    "l": (98, 112, 164),    # hair sheen
    "S": (240, 198, 158),   # skin
    "s": (204, 154, 116),   # skin shade
    "E": (34, 30, 46),      # eyes
    "N": (62, 72, 120),     # sailor top
    "n": (42, 48, 84),      # top shade
    "W": (232, 232, 238),   # collar stripe
    "w": (150, 160, 196),   # collar stripe on the back, softer
    "R": (208, 46, 54),     # scarf
    "r": (150, 28, 40),     # scarf shade
    "B": (64, 62, 72),      # belt
    "K": (48, 54, 90),      # skirt
    "k": (32, 36, 62),      # skirt pleat / hem
    "T": (172, 118, 64),    # geta wood
    "t": (112, 72, 40),     # geta shade
}

# Recoloring for every frame
RECOLOR = {
    C_HAIR: PAL["h"], C_HAIR_SHADE: (34, 34, 58), C_DARK: PAL["H"], C_SHINE: PAL["l"], C_HAIR_DARK2: PAL["H"],
    C_SUIT: PAL["N"], C_SUIT_DARK: PAL["n"], C_MARK: PAL["n"],
    C_SKIN: PAL["S"], C_SKIN_SHADE: PAL["s"], C_EYE: PAL["E"], C_BLACK: C_BLACK,
}

# Heads, drawn in the coordinates of the Convict frame named with each (22 x 28). '.' leaves the frame alone.
HEADS = {
    "front": ("convict_idle_front_001", """
......................
......................
......................
......................
......................
......................
......................
.......HHHHHHHH.......
.....HHhhhhhlllhH.....
....HhhhhhhlllhhhH....
....HhhHhhllhhhHhhH...
...HhhHhhllhhhhhHhH...
...HhHhhHhhHhhHhhhH...
...HhHSHhSHhhHSHhhH...
...HhHSSSSSHSSSSHhH...
...HhSSESSSSSSESShH...
...HhSSSSSSSSSSSShH...
...HhHsSSSSSSSSsHhH...
...HhH.ssssssss.HhH...
...H.H...........H.H..
"""),
    "front34": ("convict_idle_001", """
......................
......................
......................
......................
......................
......................
......................
......................
......HHHHHHHHHH......
.....HhhhhhlllhhHH....
....HhhhhhlllhhhhhH...
....HhhHhllhhhHhhhhH..
....HhHhhhhHhhhhHhhH..
....HhHShhHSShHhhHhH..
....HhSSSSHSSSSShHhH..
....HhSSSESSSSSEhhH...
....HhSSSSSSSSSShhH...
....HhHsSSSSSSShhhH...
....HhH.sssssss.hhH...
....H.H.........H.H...
"""),
    "back": ("convict_idle_back_001-001", """
......................
......................
......................
......................
......................
......................
......................
.......HHHHHHHH.......
.....HHhhhhhlllhH.....
....HhhhhhhlllhhhH....
....HhhhhhllhhhhhhH...
...HhhhhhllhhhhhhhH...
...HhhhhllhhhhhhhhH...
...HhhhhhhhhhhhhhhH...
...HhhhhhhhhhhhhhhH...
...HhhhhhhhhhhhhhhH...
...HhhhHhhhHhhhHhhH...
...HhhhHhhhHhhhHhhH...
...HhHhHhHhHhHhHhhH...
...H.H...........H.H..
"""),
    "back34": ("convict_idle_back_001", """
......................
......................
......................
......................
......................
......................
......................
......................
......HHHHHHHHHH......
.....HhhhhhlllhhHH....
....HhhhhhlllhhhhhH...
....HhhhhllhhhhhhhhH..
....HhhhllhhhhhhhhhH..
....HhhhhhhhhhhhhhhH..
....HhhhhhhhhhhHhhhH..
....HhhhHhhhhhhHShhH..
....HhhhHhhhhHhHSShH..
....HhHhHhhhhHhHSsH...
....HhHhHhHhhHhHsH....
....H.H.H...H.H.......
"""),
}

# Clips rebuilt with a head, by the way the character faces
DIRECTIONS = {
    "front": ["idle_forward", "idle_forward_hand", "idle_forward_twohands", "run_down", "run_down_hand", "run_down_twohands"],
    "front34": ["idle", "idle_hand", "idle_twohands", "run_right", "run_right_hand", "run_right_twohands"],
    "back": ["idle_backward", "idle_backward_hand", "idle_backward_twohands", "run_up", "run_up_hand", "run_up_twohands"],
    "back34": ["idle_bw", "idle_bw_twohands", "run_right_bw", "run_right_bw_twohands"],
}
FACING_FRONT = {"front", "front34"}
# Dodges: the upright first and last frames get a head, and the crouch while the face shows; the roll is recolored.
DODGE_ENDS = {"dodge": "front", "dodge_bw": "back", "dodge_left": "front34", "dodge_left_bw": "back34"}


def pixels_of(im, colors):
    return [(x, y) for y in range(im.height) for x in range(im.width) if im.getpixel((x, y))[3] and im.getpixel((x, y))[:3] in colors]


def anchor(im, direction):
    """Where the head is: the middle of his eyes (front views), else the middle of his hair's bottom row."""
    if direction in FACING_FRONT:
        eyes = pixels_of(im, {C_EYE})
        if eyes:
            return (round(sum(x for x, _ in eyes) / len(eyes)), round(sum(y for _, y in eyes) / len(eyes)))
    hair = pixels_of(im, HAIR_ONLY)
    if not hair:
        return None
    bottom = max(y for _, y in hair)
    xs = [x for x, y in hair if y >= bottom - 1]
    return ((min(xs) + max(xs)) // 2, bottom)


def head_box(im):
    hair = pixels_of(im, HAIR_ONLY)
    if not hair:
        return None
    return (min(x for x, _ in hair), min(y for _, y in hair), max(x for x, _ in hair), max(y for _, y in hair))


def recolor(im):
    out = im.copy()
    for y in range(im.height):
        for x in range(im.width):
            p = im.getpixel((x, y))
            if p[3] and p[:3] == C_DARK and face_line(im, x, y):
                out.putpixel((x, y), PAL["s"] + (p[3],))
                continue
            if p[3]:
                c = RECOLOR.get(p[:3])
                if c:
                    out.putpixel((x, y), c + (p[3],))
    return out


def face_line(im, x, y):
    """His dark brown between skin pixels (mouth, nose, jaw) rather than at the hair."""
    skin = hair = 0
    for nx, ny in ((x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1)):
        if 0 <= nx < im.width and 0 <= ny < im.height:
            q = im.getpixel((nx, ny))
            if q[3] and q[:3] in SKIN_COLORS:
                skin += 1
            elif q[3] and q[:3] in HAIR_ONLY:
                hair += 1
    return skin >= 2 and hair == 0


def put(im, x, y, letter):
    if 0 <= x < im.width and 0 <= y < im.height:
        im.putpixel((x, y), PAL[letter] + (255,))


def rebuild(src, direction, refs):
    """src: Convict's frame. Returns Sukeban's frame of the same size."""
    w, h = src.size
    a = anchor(src, direction)
    box = head_box(src)
    if a is None or box is None:
        return recolor(src)
    # the body, before the head is taken out: which rows are suit
    suit_rows = sorted({y for _, y in pixels_of(src, SUIT_COLORS)})
    if not suit_rows:
        return recolor(src)
    top = suit_rows[0]
    bottom = max(y for y in range(h) for x in range(w) if src.getpixel((x, y))[3])
    out = Image.new("RGBA", src.size, (0, 0, 0, 0))
    # body: everything that is neither hair nor his face
    x0, y0, x1, y1 = box
    # his head reaches down to the jaw: three rows under the eyes (front views), a row under the hair (back views)
    if direction in FACING_FRONT and pixels_of(src, {C_EYE}):
        head_bottom = a[1] + 3
    else:
        head_bottom = y1 + 1
    for y in range(h):
        for x in range(w):
            p = src.getpixel((x, y))
            if not p[3]:
                continue
            c = p[:3]
            in_head = x0 - 2 <= x <= x1 + 2 and y0 <= y <= head_bottom
            if c in HAIR_ONLY or c == C_EYE or (in_head and c not in SUIT_COLORS):
                continue  # hair, face, ears, jaw
            if c in (C_DARK, C_HAIR_DARK2):
                p = SKIN_OUTLINE + (p[3],)  # a hand's outline
            out.putpixel((x, y), p)
    body(out, src, direction, top, bottom)
    # head
    tmpl, ref_anchor = refs[direction]
    dx, dy = a[0] - ref_anchor[0], a[1] - ref_anchor[1]
    for ty, row in enumerate(tmpl):
        for tx, letter in enumerate(row):
            if letter != ".":
                put(out, tx + dx, ty + dy, letter)
    return out


def body(out, src, direction, top, bottom):
    """The jumpsuit below the head becomes the uniform (out already holds Convict's body pixels, unrecolored)."""
    w, h = out.size
    belt = top + 3
    front = direction in FACING_FRONT

    def color(x, y):
        p = out.getpixel((x, y))
        return p[:3] if p[3] else None

    for y in range(top, bottom + 1):
        row = [x for x in range(w) if color(x, y)]
        if not row:
            continue
        left, right = min(row), max(row)
        suit_x = [x for x in row if color(x, y) in SUIT_COLORS]
        if y < belt:
            for x in row:
                c = color(x, y)
                if c == C_SUIT:
                    put(out, x, y, "N")
                elif c in (C_SUIT_DARK, C_MARK):
                    put(out, x, y, "n")
                elif c in SKIN_COLORS:
                    if suit_x and min(suit_x) < x < max(suit_x) and y <= top + 1 and front:
                        put(out, x, y, "R" if y == top else "r")  # the neck opening: scarf
                    else:
                        put(out, x, y, "S" if c == C_SKIN else "s")
            if front and y == top:
                scarf = [x for x in row if out.getpixel((x, y))[:3] == PAL["R"]]
                if scarf:
                    put(out, min(scarf) - 1, y, "W")
                    put(out, max(scarf) + 1, y, "W")
            if not front and suit_x and y - top == 1:
                for x in range(min(suit_x) + 1, max(suit_x)):
                    put(out, x, y, "w")  # the stripe of the collar's flap
        elif y == belt:
            for x in row:
                c = color(x, y)
                if c in SUIT_COLORS:
                    put(out, x, y, "B")
                elif c in SKIN_COLORS:
                    put(out, x, y, "S" if c == C_SKIN else "s")
        elif y < bottom:
            # long skirt: suit and the gap between the legs; hands keep their skin
            suit_row = [x for x in row if color(x, y) in SUIT_COLORS] or row
            l, r = min(suit_row), max(suit_row)
            if y >= bottom - 2:
                l, r = l - 1, r + 1  # flares at the hem
            for x in range(l, r + 1):
                c = color(x, y) if 0 <= x < w else None
                if c in SKIN_COLORS and not (l < x < r):
                    put(out, x, y, "S" if c == C_SKIN else "s")
                    continue
                letter = "k" if y == bottom - 1 or (x - l) % 3 == 2 else "K"
                put(out, x, y, letter)
            for x in row:
                if (x < l or x > r) and color(x, y) in SKIN_COLORS:
                    put(out, x, y, "S" if color(x, y) == C_SKIN else "s")
        else:
            # the feet: geta
            for x in row:
                c = color(x, y)
                if c in SKIN_COLORS:
                    put(out, x, y, "T")
                    if 0 <= x - 1 < w and not out.getpixel((x - 1, y))[3]:
                        put(out, x - 1, y, "t")
                elif c in SUIT_COLORS:
                    put(out, x, y, "k")
    # anything still in Convict's colors (stray suit pixels)
    for y in range(h):
        for x in range(w):
            p = out.getpixel((x, y))
            if p[3] and p[:3] in RECOLOR:
                out.putpixel((x, y), RECOLOR[p[:3]] + (p[3],))


def main(argv):
    preview = argv[argv.index("--preview") + 1] if "--preview" in argv else None
    defs = tk2d.definitions(os.path.join(COLLECTION, "Convict.prefab"))
    clips = tk2d.clips(LIBRARY)
    atlas = Image.open(os.path.join(COLLECTION, "atlas0.png")).convert("RGBA")
    by_name = {d.name: d for d in defs}

    def frame(d):
        im = atlas.crop(d.rect(*atlas.size))
        return im.transpose(Image.Transpose.ROTATE_90) if d.flipped else im

    refs = {}
    for direction, (ref_name, art) in HEADS.items():
        rows = art.strip("\n").split("\n")
        assert all(len(r) == 22 for r in rows), direction
        refs[direction] = (rows, anchor(frame(by_name[ref_name]), direction))

    sheet = atlas.copy()
    done = {}
    for direction, names in DIRECTIONS.items():
        for clip in names:
            for sprite_id, _ in clips[clip]:
                if sprite_id not in done:
                    done[sprite_id] = rebuild(frame(defs[sprite_id]), direction, refs)
    for clip, direction in DODGE_ENDS.items():
        frames = clips[clip]
        for k, (sprite_id, _) in enumerate(frames):
            src = frame(defs[sprite_id])
            # the upright ends always; in between where he still faces us with both eyes (crouching into the roll)
            facing = direction in FACING_FRONT and len(pixels_of(src, {C_EYE})) >= 2
            if sprite_id not in done and (k in (0, len(frames) - 1) or facing):
                done[sprite_id] = rebuild(src, direction, refs)
    for d in defs:
        if not d.uvs:
            continue
        im = done.get(d.index) or recolor(frame(d))
        if d.flipped:
            im = im.transpose(Image.Transpose.ROTATE_270)
        sheet.paste(im, d.rect(*atlas.size)[:2])
    sheet.save(OUT)
    print(f"{len(done)} frames rebuilt, {len(defs) - len(done)} recolored -> {os.path.relpath(OUT, ROOT)}")

    if preview:
        os.makedirs(preview, exist_ok=True)
        rows = [c for names in DIRECTIONS.values() for c in names] + ["dodge", "dodge_bw", "dodge_left", "dodge_left_bw"]
        sc, cw, ch = 4, 26 * 4, 30 * 4
        img = Image.new("RGBA", (9 * cw, len(rows) * ch), (58, 52, 50, 255))
        for r, clip in enumerate(rows):
            for k, (sprite_id, _) in enumerate(clips[clip]):
                d = defs[sprite_id]
                im = sheet.crop(d.rect(*atlas.size))
                if d.flipped:
                    im = im.transpose(Image.Transpose.ROTATE_90)
                img.alpha_composite(im.resize((im.width * sc, im.height * sc), Image.NEAREST), (k * cw, r * ch))
        img.save(os.path.join(preview, "sukeban_key.png"))


if __name__ == "__main__":
    main(sys.argv[1:])
