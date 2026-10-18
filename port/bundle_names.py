"""Finds the game's bundle assets AssetRipper exported under another name, writes port/bundle_renames.tsv.

    python port/bundle_names.py      (needs UnityPy; the original bundles in ripped/_removed/StreamingAssets_Assets)

A bundle finds an asset by its path or file name (AssetBundle.LoadAsset), and mods load the game's assets by their
original names (Expand the Gungeon: "basic special rooms (shrines, etc)", `LoadAsset<Shader>("BraveLitTk2dCustomFalloffCutout")`).
AssetRipper changed some on export:
  - "," in a file name became "_";
  - an asset that was both in Resources/ and in a bundle became two files, the bundle's one with "_0" appended
    (37 shaders of shared_auto_001: assets/resources/shaders/bravelittk2dcustomfalloff_0.shader).
For each original container path of each bundle whose file in the project (tagged with that bundle) has another
name, the table maps the project file to the original path - or, when the original path is taken (the Resources
copy), to Assets/BundleNames/<bundle>/<original file name> (flat: a Resources folder anywhere would put it into the
player too): another folder, the original file name, so
lookups by name find it again (a full-path lookup would still get nothing; no mod does that so far).
apply.sh moves the files with their .meta (the GUIDs, and every reference, stay).
"""
import os
import re
import sys

import UnityPy

PORT = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(PORT)
ORIGINAL = os.path.join(ROOT, "ripped", "_removed", "StreamingAssets_Assets")
PROJECT = os.path.join(ROOT, "ripped", "Unity6Project")
OUT = os.path.join(PORT, "bundle_renames.tsv")
SANITIZED = str.maketrans({",": "_"})


def bundle_of(meta):
    with open(meta, encoding="utf-8", errors="replace") as f:
        m = re.search(r"^  assetBundleName: (.*)$", f.read(), re.M)
    return m.group(1).strip() if m else ""


def main():
    files = {}
    for root, _, names in os.walk(os.path.join(PROJECT, "Assets")):
        for n in names:
            if not n.endswith(".meta") and os.path.exists(os.path.join(root, n + ".meta")):
                rel = os.path.relpath(os.path.join(root, n), PROJECT).replace(os.sep, "/")
                files[rel.lower()] = rel
    rows = []
    for root, _, names in os.walk(ORIGINAL):
        for n in names:
            if n.endswith(".manifest"):
                continue
            bundle = os.path.relpath(os.path.join(root, n), ORIGINAL).replace(os.sep, "/")
            for path in UnityPy.load(os.path.join(root, n)).container:
                current = files.get(path)
                if current and bundle_of(os.path.join(PROJECT, current + ".meta")) == bundle:
                    continue
                stem, ext = os.path.splitext(path)
                found = suffixed = None
                for i, cand in enumerate((path.translate(SANITIZED),) + tuple(f"{stem.translate(SANITIZED)}_{n}{ext}" for n in range(4))):
                    rel = files.get(cand)
                    if rel and bundle_of(os.path.join(PROJECT, rel + ".meta")) == bundle:
                        found, suffixed = rel, i > 0
                        break
                if not found:
                    continue  # scenes (bundled by EtgWebBuild), .psd textures exported as .png: not loaded by name
                # Original casing: the project file's folders, the original file name from the sanitized/suffixed one.
                fstem, fext = os.path.splitext(os.path.basename(found))
                if suffixed:
                    fstem = re.sub(r"_\d+$", "", fstem)
                original_name = restore(fstem, os.path.basename(stem)) + fext
                target = os.path.dirname(found) + "/" + original_name
                if target.lower() in files and target.lower() != found.lower():
                    target = "Assets/BundleNames/" + bundle + "/" + original_name  # flat: no Resources/ folder in the path
                rows.append((found, target))
    with open(OUT, "w", encoding="utf-8", newline="\n") as f:
        f.write("# project file\tnew path (port/bundle_names.py; apply.sh moves them with their .meta)\n")
        for a, b in sorted(rows):
            f.write(f"{a}\t{b}\n")
    print(f"{len(rows)} renames -> {OUT}")


def restore(project_stem, original_lower):
    """The project file name's casing with the original's characters ("Shrines_ Etc" + "shrines, etc" -> "Shrines, Etc")."""
    if project_stem.lower().translate(SANITIZED) == original_lower.translate(SANITIZED) and len(project_stem) == len(original_lower):
        return "".join(o if p == "_" and o != "_" else p for p, o in zip(project_stem, original_lower))
    return project_stem


if __name__ == "__main__":
    if len(sys.argv) != 1:
        print(__doc__)
        sys.exit(2)
    main()
