"""Reads tk2d data exported by AssetRipper: a sprite collection (Collection.prefab + atlas0.png) and an animation library.

Used by the Sukeban sprite tools (extract_frames.py, build_sheet.py). Only what they need: definition names, atlas
rectangles (rotated or not) and the clips' sprite ids.
"""
import re


class Definition:
    def __init__(self, index, name):
        self.index = index
        self.name = name
        self.uvs = []
        self.flipped = 0

    def rect(self, atlas_w, atlas_h):
        """Atlas rectangle (left, top, right, bottom) in pixels."""
        xs = [u[0] for u in self.uvs]
        ys = [u[1] for u in self.uvs]
        return (round(min(xs) * atlas_w), round((1 - max(ys)) * atlas_h),
                round(max(xs) * atlas_w), round((1 - min(ys)) * atlas_h))


def definitions(prefab):
    """The collection's sprite definitions in order (a clip's spriteId is the index)."""
    result = []
    current = None
    in_uvs = False
    in_defs = False
    with open(prefab, encoding="utf-8") as f:
        for line in f:
            if line.startswith("  spriteDefinitions:"):
                in_defs = True
                continue
            if in_defs and re.match(r"  [a-zA-Z]", line):
                in_defs = False
            if not in_defs:
                continue
            m = re.match(r"  - name: ?(.*)$", line)
            if m:
                current = Definition(len(result), m.group(1).strip())
                result.append(current)
                in_uvs = False
                continue
            if current is None:
                continue
            if line.startswith("    uvs:"):
                in_uvs = True
                continue
            if in_uvs:
                m = re.match(r"    - \{x: ([-0-9.e]+), y: ([-0-9.e]+)\}", line)
                if m:
                    current.uvs.append((float(m.group(1)), float(m.group(2))))
                    continue
                in_uvs = False
            m = re.match(r"    flipped: (\d)", line)
            if m:
                current.flipped = int(m.group(1))
    return result


def clips(library):
    """{clip name: [(sprite id, collection guid)...]} of an animation library prefab."""
    result = {}
    name = None
    with open(library, encoding="utf-8") as f:
        for line in f:
            m = re.match(r"  - name: ?(.*)$", line)
            if m:
                name = m.group(1).strip()
                result[name] = []
                continue
            m = re.match(r"    - spriteCollection: \{fileID: -?\d+(?:, guid: (\w+))?", line)
            if m and name is not None:
                result[name].append([None, m.group(1)])
                continue
            m = re.match(r"      spriteId: (\d+)", line)
            if m and name is not None and result[name]:
                result[name][-1][0] = int(m.group(1))
    return {k: [tuple(f) for f in v] for k, v in result.items()}
