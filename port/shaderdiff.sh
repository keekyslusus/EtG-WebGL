#!/usr/bin/env bash
# Differential test of the decompiled shaders, no game run needed: every DX11 variant's original bytecode
# against the decompiled CG recompiled with d3dcompiler_47, both on D3D11 WARP with the same random inputs.
# Needs tools/uscs/dump from port/decompile_shaders.sh. Report: port/build/shaderdiff-report/report.md
#
#   bash port/shaderdiff.sh                                   # everything
#   bash port/shaderdiff.sh --shader Brave/PlayerShader      # one shader (repeatable; "Brave/*" = prefix)
#   bash port/shaderdiff.sh --shader X --variant 12          # one variant (index from the report)
# Other options: --trials 8 --seed 1 --jobs N
set -euo pipefail

PORT="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(dirname "$PORT")"
DUMP="$ROOT/tools/uscs/dump"
OUT="$PORT/build/shaderdiff-report"   # not build/shaderdiff: same dir as build/ShaderDiff on Windows
CGINC="${CGINC:-C:/Program Files/Unity/Hub/Editor/6000.6.5f1/Editor/Data/Resources/CGIncludes}"

[ -d "$DUMP" ] && [ -n "$(ls -A "$DUMP")" ] || { echo "no $DUMP - run port/decompile_shaders.sh first"; exit 1; }
log=$(dotnet build "$PORT/ShaderDiff" -c Release -o "$PORT/build/ShaderDiff" -v q --nologo 2>&1) || { echo "$log"; exit 1; }

# the decompiled text as decompile_shaders.sh produced it, through fix_shaders.py (its GLES rewrites are #if-ed out on D3D11)
rm -rf "$OUT/src"
mkdir -p "$OUT/src"
(cd "$DUMP" && find . -name '*.shader' -exec cp --parents {} "$OUT/src/" \;)
python -I "$PORT/fix_shaders.py" "$OUT/src"

win() { cygpath -m "$1"; }
"$PORT/build/ShaderDiff/ShaderDiff.exe" --dump "$(win "$DUMP")" --src "$(win "$OUT/src")" --cginc "$CGINC" --out "$(win "$OUT")" "$@"
