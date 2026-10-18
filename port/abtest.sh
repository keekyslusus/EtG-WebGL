#!/usr/bin/env bash
# D3D11-vs-GLES A/B test in the Unity editor: the same frames rendered with -force-d3d11 and -force-gles30
# (EtgAbTest.cs: every material, and every shader without one, on a quad rendered into a RenderTexture under a few
# lighting setups), then compared by abtest_report.py. The decompiled shaders are proven equal to the
# game's DX11 bytecode on D3D11 (shaderdiff.sh), so what differs here is DX11-vs-GLES.
# Output: port/build/abtest/report.html (+ report.md), the renders in port/build/abtest/<run>/.
# Unity must be closed (the project can only be open once; apply.sh renames DLLs).
#
#   bash port/abtest.sh                         # apply.sh, d3d11, gles30, d3d11 again (noise floor), report
#   bash port/abtest.sh --no-apply --no-repeat  # project already up to date, skip the determinism run
#   bash port/abtest.sh --filter Water          # a subset (regex on the asset path)
#   bash port/abtest.sh --report-only           # just rebuild the report from existing renders
# Other options: --runs "d3d11 gles30 gles30" (an API twice -> <api>.2), --tol N, --min-frac F,
#   --out DIR (default port/build/abtest)
# apply.sh runs without --mods (the vanilla game's materials); --no-apply keeps a --mods project as it is.
set -euo pipefail

PORT="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(dirname "$PORT")"
PROJECT="$ROOT/ripped/Unity6Project"
OUT="$PORT/build/abtest"
UNITY="${UNITY:-/c/Program Files/Unity/Hub/Editor/6000.6.5f1/Editor/Unity.exe}"

apply=1; runs="d3d11 gles30 d3d11"; report_only=0; unity_args=(); report_args=()
while [ $# -gt 0 ]; do
    case "$1" in
        --no-apply) apply=0 ;;
        --no-repeat) runs="d3d11 gles30" ;;
        --runs) runs="$2"; shift ;;
        --filter) unity_args+=(-abFilter "$2"); shift ;;
        --tol|--min-frac) report_args+=("$1" "$2"); shift ;;
        --out) OUT="$(cd "$(dirname "$2")" && pwd)/$(basename "$2")"; shift ;;
        --report-only) report_only=1 ;;
        *) echo "unknown option $1"; exit 1 ;;
    esac
    shift
done
win() { cygpath -w "$1"; }

if [ $report_only = 0 ]; then
    if bash "$PORT/unity_running.sh"; then
        echo "Unity is running - close it first (the project can only be open in one editor)."
        exit 1
    fi
    [ $apply = 1 ] && bash "$PORT/apply.sh"

    mkdir -p "$OUT"

    declare -A seen
    for api in $runs; do
        name="$api"
        [ -n "${seen[$api]:-}" ] && name="$api.2"
        seen[$api]=1
        dir="$OUT/$name"
        rm -rf "$dir"; mkdir -p "$dir"
        echo "== $name: Unity -force-$api (log: $dir/editor.log)"
        start=$(date +%s)
        code=0
        timeout 60m "$UNITY" -batchmode -projectPath "$(win "$PROJECT")" \
            "-force-$api" -executeMethod EtgAbTest.Run -logFile "$(win "$dir/editor.log")" \
            -abOut "$(win "$dir")" -abApi "$api" "${unity_args[@]}" || code=$?
        echo "   exit $code after $(( $(date +%s) - start )) s, $(grep -c . "$dir/manifest.jsonl" 2>/dev/null || echo 0) renders"
        grep -E "^\[AbTest\]|error CS|[A-Za-z]Exception: " "$dir/editor.log" | grep -v "^\[AbTest\] gallery:" | head -20 | sed 's/^/   /' || true
        if [ $code != 0 ]; then
            echo "Unity failed for $name, see $dir/editor.log"
            exit 1
        fi
    done
fi

python -I "$PORT/abtest_report.py" "$OUT" "${report_args[@]}"
