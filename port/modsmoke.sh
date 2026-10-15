#!/usr/bin/env bash
# Runs the mod smoke test without anyone at the editor (port/README.md, Mods -> Smoke test): opens the
# project in Unity 2017.4 through OpenGL ES 3.0 like open-gles30.ps1 (closer to the WebGL 2 player than
# D3D11), asks EtgModSmokeMenu for a run and waits until the editor exits by itself.
#   bash port/modsmoke.sh [phases]     phases: all (default) | items,shop | enemies | floors,flows ...
# port/build/modsmoke/only.txt limits the run as from the menu. Report: port/build/modsmoke/report.md.
# Watchdog: a game hang on the main thread (Editor.log silent for HANG_MINUTES) or an editor crash ends the
# attempt; the editor is killed, the Unity Bug Reporter closed without sending (it attaches the whole project),
# and the next attempt resumes after the step that was running (it is reported as "hang"); MAX_ATTEMPTS such failures.
# RESUME=1 bash port/modsmoke.sh <phases> continues an interrupted run the same way.
set -uo pipefail

PHASES="${1:-all}"
HANG_MINUTES="${HANG_MINUTES:-6}"
MAX_ATTEMPTS="${MAX_ATTEMPTS:-8}"
PORT="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(dirname "$PORT")"
UNITY="${UNITY_EXE:-/c/Program Files/Unity/Hub/Editor/2017.4.27f1/Editor/Unity.exe}"
PROJECT="$(cygpath -w "$ROOT/ripped/ExportedProject")"
LOG="$LOCALAPPDATA/Unity/Editor/Editor.log"
OUT="$ROOT/port/build/modsmoke"

unity_running() { tasklist //FI "IMAGENAME eq Unity.exe" //NH 2>/dev/null | grep -qi "^Unity.exe"; }

if unity_running; then
    echo "Unity is running - close it first." >&2
    exit 1
fi
mkdir -p "$OUT"
[ "${RESUME:-0}" = 1 ] || rm -f "$OUT/resume"   # RESUME=1: carry on from the last report.json

attempt=0
failures=0   # hangs and crashes; planned restarts ("restart before X") don't count
while [ "$failures" -lt "$MAX_ATTEMPTS" ]; do
    attempt=$((attempt + 1))
    # The editor picks the request up after its first idle script load (EtgModSmokeMenu), whichever build of
    # the script that is (not in Temp/: the editor clears that on startup).
    printf '%s\n1\n' "$PHASES" > "$OUT/request"
    echo "== Mod smoke test ($PHASES), attempt $attempt, Unity -force-gles30, log: $LOG"
    "$UNITY" -projectPath "$PROJECT" -force-gles30 &
    sleep 20
    last_size=-1
    quiet=0
    hung=0
    while unity_running; do
        size=$(stat -c %s "$LOG" 2>/dev/null || echo 0)
        if [ "$size" = "$last_size" ]; then
            quiet=$((quiet + 10))
        else
            quiet=0
            last_size=$size
        fi
        # before the test has started (import, shader compile) the editor may be quiet for longer
        limit=$((HANG_MINUTES * 60))
        grep -q "\[ModSmoke\] step" "$LOG" 2>/dev/null || limit=$((limit * 3))
        if [ "$quiet" -ge "$limit" ]; then
            echo "== no Editor.log output for $((quiet / 60)) min: the editor hangs, killing it"
            grep "\[ModSmoke\]" "$LOG" | tail -3
            taskkill //IM Unity.exe //F > /dev/null 2>&1
            hung=1
            sleep 5
            break
        fi
        sleep 10
    done
    wait 2> /dev/null
    crashed=0
    if tasklist //NH 2>/dev/null | grep -qi "UnityBugReporter"; then
        taskkill //IM UnityBugReporter.exe //F > /dev/null 2>&1
        crashed=1
        echo "== the editor crashed (bug reporter closed without sending); last test lines of Editor.log:"
        grep "\[ModSmoke\]" "$LOG" | tail -3
    fi
    state=$(grep -m1 -o "state: \*\*[^*]*\*\*" "$OUT/report.md" 2>/dev/null)
    echo "== attempt $attempt over: $state"
    if [ "$hung" = 0 ] && [ "$crashed" = 0 ] && ! echo "$state" | grep -q "running \|waiting\|starting\|restart before"; then
        exit 0
    fi
    # "restart before X": the test ended the attempt itself after a broken level load (a quick restart from there
    # has hung the game); the next one starts with X
    if ! echo "$state" | grep -q "running \|restart before"; then
        echo "== the test didn't get to its steps; not resuming" >&2
        exit 2
    fi
    if [ "$hung" = 1 ] || [ "$crashed" = 1 ] || echo "$state" | grep -q "running "; then
        failures=$((failures + 1))
    fi
    touch "$OUT/resume"
done
echo "== gave up after $MAX_ATTEMPTS hangs / crashes" >&2
exit 3
