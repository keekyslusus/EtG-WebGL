"""Packs the files the baked mods read at runtime (sprites, data...) for the web build.

Every package of modpack.txt (except BepInEx itself) lands under BepInEx/plugins/<package>/, the
layout r2modman installs, both in:
  - <pack>: one file the boot scene downloads and unpacks into the in-memory file system
    (WebShim/ModFiles.cs reads it: "ETGMODS1", int32 count, then per file int32 path length,
    UTF-8 path, int32 size, bytes; little endian);
  - <runtime dir>: the same tree on disk for the editor's Play Mode.
DLLs become empty placeholders: their code is baked into the build, but mods look for their own
folder through their DLL's location.

Entries with an absolute path go to that place in the file system: the IL2CPP resource files
(ModBaker resources) under /Il2CppData/Resources, only for the web build.

Usage: build_pack.py <modpack.txt> <mods dir> <pack> <runtime dir> [<il2cpp resources dir>]
"""
import os
import shutil
import struct
import sys


def packages(modpack):
    with open(modpack, encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if line and not line.startswith("#"):
                name = line.split()[0]
                if name != "BepInEx-BepInExPack_EtG":
                    yield name


def main(modpack, mods_dir, pack_path, runtime_dir, resources_dir=None):
    entries = []
    for pkg in packages(modpack):
        src = os.path.join(mods_dir, pkg, "plugins")
        if not os.path.isdir(src):
            continue
        for dirpath, _, files in os.walk(src):
            for name in sorted(files):
                full = os.path.join(dirpath, name)
                rel = os.path.relpath(full, src).replace(os.sep, "/")
                target = f"BepInEx/plugins/{pkg}/{rel}"
                if name.lower().endswith(".dll"):
                    data = b""
                else:
                    with open(full, "rb") as f:
                        data = f.read()
                entries.append((target, data))

    files = list(entries)
    if resources_dir and os.path.isdir(resources_dir):
        for name in sorted(os.listdir(resources_dir)):
            with open(os.path.join(resources_dir, name), "rb") as f:
                entries.append((f"/Il2CppData/Resources/{name}", f.read()))

    os.makedirs(os.path.dirname(pack_path), exist_ok=True)
    with open(pack_path, "wb") as f:
        f.write(b"ETGMODS1")
        f.write(struct.pack("<i", len(entries)))
        for path, data in entries:
            p = path.encode("utf-8")
            f.write(struct.pack("<i", len(p)))
            f.write(p)
            f.write(struct.pack("<i", len(data)))
            f.write(data)

    if os.path.isdir(runtime_dir):
        shutil.rmtree(runtime_dir)
    for path, data in files:
        full = os.path.join(runtime_dir, path)
        os.makedirs(os.path.dirname(full), exist_ok=True)
        with open(full, "wb") as f:
            f.write(data)

    size = os.path.getsize(pack_path)
    print(f"   {len(entries)} mod files, pack {size / 1024:.0f} KB -> {pack_path}")


if __name__ == "__main__":
    if len(sys.argv) not in (5, 6):
        print(__doc__)
        sys.exit(2)
    main(*sys.argv[1:])
