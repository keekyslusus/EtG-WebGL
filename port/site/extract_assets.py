"""Extracts the pause-screen art for the web page (port/site) from the ripped project.

Writes port/site/assets/:
  pause_anim.png     every pause_screen_* frame from GameUIAtlas (bats, ghost, blob...), packed
  pause_anim.json    per animation: frames (x, y, w, h in pause_anim.png), position on the
                     pause border (pixels, top-left), length in seconds and loop type
  04B_03.ttf         the game's pixel UI font
Positions/timings come from the dfSprite/dfSpriteAnimation components in UI Root.prefab
(dfGUI lays the pause menu out at 3x, so margins are divided by 3).

Usage: python port/site/extract_assets.py
"""
import json
import os
import re
import shutil

from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
ASSETS = os.path.normpath(os.path.join(HERE, "../../ripped/ExportedProject/Assets"))
OUT = os.path.join(HERE, "assets")
UI = os.path.join(ASSETS, "art/ui")
PAUSE = os.path.join(UI, "pause screen")
SCALE = 3


def meta_guid(path):
    with open(path + ".meta", encoding="utf8") as f:
        return re.search(r"^guid: (\w+)", f.read(), re.M).group(1)


def parse_docs(path):
    """Unity YAML -> {fileID: body}."""
    with open(path, encoding="utf8") as f:
        text = f.read()
    docs = {}
    for m in re.finditer(r"^--- !u!\d+ &(\d+)\n(.*?)(?=^--- |\Z)", text, re.M | re.S):
        docs[m.group(1)] = m.group(2)
    return docs


def field(body, name):
    m = re.search(r"^\s*" + name + r": (.*)$", body, re.M)
    return m.group(1).strip() if m else None


def margins(body):
    m = re.search(r"margins:\s*\n\s*left: ([-\d.e]+)\s*\n\s*top: ([-\d.e]+)", body)
    return float(m.group(1)), float(m.group(2))


def atlas_sprites():
    with open(os.path.join(UI, "GameUIAtlas.prefab"), encoding="utf8") as f:
        text = f.read()
    sprites = {}
    pat = (r"- name: (\S+)\n\s*region:\n\s*serializedVersion: \d+\n\s*x: ([\d.e-]+)\n\s*y: ([\d.e-]+)\n"
           r"\s*width: ([\d.e-]+)\n\s*height: ([\d.e-]+)[\s\S]*?rotated: (\d)\n\s*sizeInPixels: \{x: (\d+), y: (\d+)\}")
    for m in re.finditer(pat, text):
        name = m.group(1)
        x, y, w, h = (float(m.group(i)) for i in range(2, 6))
        assert m.group(6) == "0", name
        sprites[name] = (x, y, w, h, int(m.group(7)), int(m.group(8)))
    return sprites


def main():
    os.makedirs(OUT, exist_ok=True)
    border_guid = meta_guid(os.path.join(PAUSE, "white_border_pause_base_001.png"))
    docs = parse_docs(os.path.join(UI, "UI Root.prefab"))

    border = next(b for b in docs.values() if border_guid in (field(b, "texture") or ""))
    bx, by = margins(border)

    clips = {}
    for name in os.listdir(PAUSE):
        if name.startswith("pause_screen_") and name.endswith(".prefab"):
            path = os.path.join(PAUSE, name)
            with open(path, encoding="utf8") as f:
                frames = re.findall(r"^  - (\S+)$", f.read(), re.M)
            clips[meta_guid(path)] = (name[len("pause_screen_"):-len(".prefab")], frames)

    anims = []
    for body in docs.values():
        clip = re.search(r"^  clip: \{.*guid: (\w+)", body, re.M)
        if not clip or clip.group(1) not in clips or "memberInfo" not in body:
            continue
        name, frames = clips[clip.group(1)]
        sprite = docs[re.search(r"Component: \{fileID: (\d+)\}", body).group(1)]
        sx, sy = margins(sprite)
        anims.append({
            "name": name,
            "frames": frames,
            "x": round((sx - bx) / SCALE),
            "y": round((sy - by) / SCALE),
            "length": float(field(body, "length")),
            "loopType": int(field(body, "loopType")),
            "loopFrom": int(field(body, "LoopSectionFrameTarget")),
            "loopFirstLength": float(field(body, "LoopSectionFirstLength")),
            "loopDelay": [float(field(body, "LoopDelayMin")), float(field(body, "LoopDelayMax"))],
        })
    anims.sort(key=lambda a: a["name"])

    atlas = Image.open(os.path.join(UI, "GameUIAtlas.png")).convert("RGBA")
    W, H = atlas.size
    sprites = atlas_sprites()
    sheet_w, rows, cx, cy, row_h = 1024, [], 0, 0, 0
    placed = {}
    for a in anims:
        for f in a["frames"]:
            if f in placed:
                continue
            x, y, w, h, pw, ph = sprites[f]
            if cx + pw > sheet_w:
                cx, cy, row_h = 0, cy + row_h + 1, 0
            # dfAtlas regions are UVs with the origin at the bottom left
            crop = atlas.crop((round(x * W), round(H - (y + h) * H), round(x * W) + pw, round(H - (y + h) * H) + ph))
            placed[f] = (cx, cy, pw, ph, crop)
            cx += pw + 1
            row_h = max(row_h, ph)
    sheet = Image.new("RGBA", (sheet_w, cy + row_h), (0, 0, 0, 0))
    for cx, cy, pw, ph, crop in placed.values():
        sheet.paste(crop, (cx, cy))
    sheet.save(os.path.join(OUT, "pause_anim.png"))
    for a in anims:
        a["frames"] = [list(placed[f][:4]) for f in a["frames"]]

    base = Image.open(os.path.join(PAUSE, "white_border_pause_base_001.png"))
    with open(os.path.join(OUT, "pause_anim.json"), "w") as f:
        json.dump({"width": base.width, "height": base.height, "anims": anims}, f, indent=1)
    shutil.copy(os.path.join(UI, "04B_03__.ttf"), os.path.join(OUT, "04B_03.ttf"))
    for a in anims:
        print("%-10s %2d frames at (%d, %d) len %.2fs loop %d from %d" % (
            a["name"], len(a["frames"]), a["x"], a["y"], a["length"], a["loopType"], a["loopFrom"]))


if __name__ == "__main__":
    main()
