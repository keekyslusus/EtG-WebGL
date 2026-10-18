#!/usr/bin/env bash
# Reflects over every method of the patched / mod-baked DLLs in Assets/Plugins with the editor's own Mono 2.x
# (port/MonoSigCheck). A method whose GetParameters() aborts Mono would crash the editor as soon as it shows up
# in an exception's stack trace. Crashed methods are reported and skipped, then the run continues.
#
# Usage: bash port/monosigcheck.sh [dll...]   (default: every DLL in Assets/Plugins)
set -u
PORT="$(cd "$(dirname "$0")" && pwd)"
ETG="$(dirname "$PORT")"
DATA="$ETG/tools/unity-2017.4.27f1/Editor/Data"
PLUGINS="$ETG/ripped/Unity6Project/Assets/Plugins"
OUT="$PORT/build/monosigcheck"
mkdir -p "$OUT"

w() { cygpath -w "$1"; }

export MONO_CFG_DIR="$(w "$DATA/Mono/etc")"
export MONO_PATH="$(w "$DATA/Mono/lib/mono/2.0");$(w "$DATA/Managed");$(w "$DATA/Managed/UnityEngine");$(w "$DATA/UnityExtensions/Unity/GUISystem");$(w "$DATA/UnityExtensions/Unity/Timeline/Runtime");$(w "$DATA/UnityExtensions/Unity/Networking")"
MONO="$DATA/Mono/bin/mono.exe"

"$MONO" "$(w "$DATA/Mono/lib/mono/2.0/gmcs.exe")" -nologo -out:"$(w "$OUT/MonoSigCheck.exe")" \
    "$(w "$PORT/MonoSigCheck/MonoSigCheck.cs")" || exit 1

if [ $# -gt 0 ]; then dlls=("$@"); else dlls=("$PLUGINS"/*.dll); fi
args=()
for d in "${dlls[@]}"; do args+=("$(w "$d")"); done

: > "$OUT/skip.txt"
: > "$OUT/crashes.txt"
for attempt in $(seq 1 200); do
    "$MONO" "$(w "$OUT/MonoSigCheck.exe")" "$(w "$OUT/progress.txt")" "$(w "$OUT/skip.txt")" "${args[@]}" \
        > "$OUT/run.log" 2>&1
    last="$(tail -n 1 "$OUT/progress.txt" 2>/dev/null)"
    if [ "$last" = "DONE" ]; then
        grep -v "^checked " "$OUT/run.log"
        grep "^checked " "$OUT/run.log"
        n=$(wc -l < "$OUT/crashes.txt")
        echo "Mono crashes: $n (details in $OUT/crashes.txt)"
        cat "$OUT/crashes.txt"
        [ "$n" = 0 ]
        exit $?
    fi
    [ -z "$last" ] && { echo "no progress written; run.log:"; cat "$OUT/run.log"; exit 1; }
    reason="$(grep -m1 -iE "implement me|assert|overflow|exception" "$OUT/run.log" | tr -d '\r')"
    echo "CRASH $last :: $reason" | tee -a "$OUT/crashes.txt"
    echo "$last" | cut -d' ' -f1-2 >> "$OUT/skip.txt"
done
echo "too many crashes" >&2
exit 1
