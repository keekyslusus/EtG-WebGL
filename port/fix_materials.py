"""Restores texture references AssetRipper dropped from exported materials (run by apply.sh).

material_textures.tsv lists them: material, texture property, texture - paths relative to the project.
A slot is only filled while it is empty (fileID 0), with the texture's GUID from its .meta.

Found so far: the loading screen's dfGUI atlas ("Loading Atlas", the only material of sharedassets0) came out
with an empty _MainTex, so the throbber in the corner of every loading screen drew as a white rectangle.

usage: fix_materials.py <material_textures.tsv> <Unity project dir>
"""
import os
import re
import sys


def texture_guid(path):
    with open(path + ".meta", encoding="utf-8") as f:
        m = re.search(r"^guid: ([0-9a-f]{32})", f.read(), re.M)
    if not m:
        raise SystemExit("no guid in " + path + ".meta")
    return m.group(1)


def main():
    table, project = sys.argv[1], sys.argv[2]
    fixed = 0
    with open(table, encoding="utf-8") as f:
        rows = [l.rstrip("\r\n").split("\t") for l in f if l.strip() and not l.startswith("#")]
    for material, prop, texture in rows:
        mat_path = os.path.join(project, material)
        tex_path = os.path.join(project, texture)
        if not os.path.exists(mat_path) or not os.path.exists(tex_path):
            print("   material fix skipped (missing): " + material)
            continue
        with open(mat_path, encoding="utf-8", newline="") as f:
            text = f.read()
        slot = re.compile(r"(- " + re.escape(prop) + r":\r?\n\s+m_Texture: )\{fileID: 0\}")
        ref = "{fileID: 2800000, guid: %s, type: 3}" % texture_guid(tex_path)
        new, n = slot.subn(lambda m: m.group(1) + ref, text)
        if n:
            with open(mat_path, "w", encoding="utf-8", newline="") as f:
                f.write(new)
            fixed += 1
    if fixed:
        print("   materials with restored textures: %d" % fixed)


if __name__ == "__main__":
    main()
