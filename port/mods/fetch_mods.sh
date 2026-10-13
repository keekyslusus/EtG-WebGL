#!/usr/bin/env bash
# Copies the packages listed in modpack.txt from the r2modman cache into etg/mods/<package>.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(dirname "$(dirname "$HERE")")"
CACHE="${R2MODMAN_CACHE:-$APPDATA/r2modmanPlus-local/ETG/cache}"
OUT="$ROOT/mods"
mkdir -p "$OUT"
grep -vE '^\s*(#|$)' "$HERE/modpack.txt" | while read -r pkg ver; do
    src="$CACHE/$pkg/$ver"
    [ -d "$src" ] || { echo "missing $src (install it with r2modman first)" >&2; exit 1; }
    rm -rf "$OUT/$pkg"
    cp -r "$src" "$OUT/$pkg"
    echo "   $pkg $ver"
done
