#!/usr/bin/env bash
# Decompiles every shader in the game (main asset files + all AssetBundles) from DX11
# bytecode to ShaderLab/CG with USCSandbox (github.com/nesrak1/USCSandbox).
# AssetRipper 2.0's "Decompilation" mode silently writes dummy shaders for this game,
# so apply.sh replaces those dummies with this output (matched by shader name).
#
# Needs USCSandbox built into tools/uscs (dotnet build -c Release -o tools/uscs, plus
# Files/classdata.tpk copied next to the exe). Output: tools/uscs/out/<shader name>.shader
# Also tools/uscs/dump/: the same text before fix_shaders.py plus the original DXBC of every
# variant with its parameter layout - input for port/shaderdiff.sh.
set -euo pipefail

PORT="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(dirname "$PORT")"
USCS="$ROOT/tools/uscs"
DATA="$ROOT/Enter the Gungeon/EtG_Data"
VERSION="2017.4.27f1"

[ -x "$USCS/USCSandbox.exe" ] || { echo "USCSandbox not found in $USCS"; exit 1; }
cd "$USCS"   # USCSandbox reads classdata.tpk and writes out/ relative to the working dir
mkdir -p out dump
rm -rf out/* dump/* dbg_*.bin   # clear contents only; the folder itself may be held open on Windows
win() { cygpath -m "$1"; }
# USCSandbox formats floats with the current culture: on a Russian Windows "Offset 0.125, -1"
# came out as "Offset 0,125, -1". Force invariant formatting.
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1

count=0
for f in resources.assets sharedassets0.assets globalgamemanagers.assets; do
    n=$(./USCSandbox.exe null "$(win "$DATA/$f")" --all --version "$VERSION" --dump dump | grep -c ' decompiled$' || true)
    echo "   $f: $n"; count=$((count + n))
done

while IFS= read -r -d '' bundle; do
    rel="${bundle#"$DATA/StreamingAssets/Assets/"}"
    for cab in $(./USCSandbox.exe "$(win "$bundle")" | sed -n 's/^  //p' | tr -d '\r'); do
        n=$(./USCSandbox.exe "$(win "$bundle")" "$cab" --all --version "$VERSION" --dump dump | grep -c ' decompiled$' || true)
        [ "$n" = "0" ] || echo "   $rel: $n"
        count=$((count + n))
    done
done < <(find "$DATA/StreamingAssets/Assets" -type f -print0)

python -I "$PORT/fix_shaders.py" "$USCS/out"
echo "== $count shaders decompiled, $(find out -name '*.shader' | wc -l) unique -> $USCS/out"
