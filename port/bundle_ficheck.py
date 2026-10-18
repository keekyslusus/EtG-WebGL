"""Compares the FullInspector data of two builds of the game's AssetBundles (e.g. Unity 2017.4's webbuild/ against
Unity 6.6's), bundle by bundle: every MonoBehaviour with FullInspector's serialized state (_serializedStateKeys /
_serializedStateValues / _objectReferences). Unity 6's prefab import once wiped that state silently (BossStatues lost
levelData, port/tasks/unity6-migration.md); a wiped object shows up here as a missing or changed entry.

Usage: python -I port/bundle_ficheck.py <old bundle dir> <new bundle dir>   (needs UnityPy)
Exit code 1 when an entry differs. Object references are compared by count and by how many are set, not by
target: path IDs differ between builds.
"""
import hashlib
import sys
from collections import Counter
from pathlib import Path

import UnityPy


def bundle_files(root: Path):
    for f in sorted(root.rglob("*")):
        if f.is_file() and f.suffix not in (".manifest", ".meta") and f.name != root.name:
            yield f.relative_to(root).as_posix()


def entries(path: Path) -> Counter:
    env = UnityPy.load(str(path))
    found = Counter()
    for obj in env.objects:
        if obj.type.name != "MonoBehaviour":
            continue
        try:
            tree = obj.read_typetree()
        except Exception:
            continue
        if "_serializedStateKeys" not in tree:
            continue
        name = ""
        go = tree.get("m_GameObject") or {}
        try:
            if go.get("m_PathID"):
                name = obj.assets_file.objects[go["m_PathID"]].read().m_Name
        except Exception:
            pass
        name = name or tree.get("m_Name", "")
        refs = tree.get("_objectReferences") or []
        values = tree.get("_serializedStateValues") or []
        digest = hashlib.sha1("\0".join(values).encode("utf-8")).hexdigest()[:12]
        found[(name, tuple(tree["_serializedStateKeys"]), digest, len(refs),
               sum(1 for r in refs if r.get("m_PathID")))] += 1
    return found


def main(old_root: str, new_root: str) -> int:
    old, new = Path(old_root), Path(new_root)
    names = sorted(set(bundle_files(old)) | set(bundle_files(new)))
    bad = 0
    total = 0
    for name in names:
        if not (old / name).exists() or not (new / name).exists():
            print(f"{name}: only in {'new' if (new / name).exists() else 'old'}")
            continue
        a, b = entries(old / name), entries(new / name)
        total += sum(b.values())
        if a == b:
            print(f"{name}: {sum(a.values())} FullInspector objects, same")
            continue
        bad += 1
        print(f"{name}: old {sum(a.values())}, new {sum(b.values())} FullInspector objects - DIFFER")
        for k in sorted((a - b).keys())[:20]:
            print(f"   only old: {k[0]!r} keys={list(k[1])} values={k[2]} refs={k[4]}/{k[3]}")
        for k in sorted((b - a).keys())[:20]:
            print(f"   only new: {k[0]!r} keys={list(k[1])} values={k[2]} refs={k[4]}/{k[3]}")
    print(f"{total} FullInspector objects in the new bundles; {bad} bundles differ")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1], sys.argv[2]))
