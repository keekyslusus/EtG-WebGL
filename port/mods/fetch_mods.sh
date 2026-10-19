#!/usr/bin/env bash
# Copies the packages listed in modpack.txt from the r2modman cache into etg/mods/<package>.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(dirname "$(dirname "$HERE")")"
CACHE="${R2MODMAN_CACHE:-$APPDATA/r2modmanPlus-local/ETG/cache}"
OUT="$ROOT/mods"
mkdir -p "$OUT"
grep -vE '^\s*(#|$)' "$HERE/modpack.txt" | while read -r pkg ver; do
    [ "$ver" = local ] && continue   # built from port/mods/<pkg> by ModBaker/bake.sh
    src="$CACHE/$pkg/$ver"
    [ -d "$src" ] || { echo "missing $src (install it with r2modman first)" >&2; exit 1; }
    rm -rf "$OUT/$pkg"
    cp -r "$src" "$OUT/$pkg"
    # A package without a plugins/ folder (Expand the Gungeon) has its files at the top level;
    # r2modman installs those into BepInEx/plugins/<package>/ as well. Same layout here.
    if [ "$pkg" != "BepInEx-BepInExPack_EtG" ] && [ ! -d "$OUT/$pkg/plugins" ]; then
        mkdir "$OUT/$pkg/plugins"
        find "$OUT/$pkg" -mindepth 1 -maxdepth 1 ! -name plugins ! -name manifest.json ! -name README.md \
            ! -name CHANGELOG.md ! -name icon.png -exec mv {} "$OUT/$pkg/plugins/" \;
    fi
    echo "   $pkg $ver"
done
