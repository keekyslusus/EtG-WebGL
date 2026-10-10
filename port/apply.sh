#!/usr/bin/env bash
# Builds WebShim + Patcher, patches the game's original assemblies and installs
# everything into the AssetRipper-exported Unity project. Safe to re-run.
set -euo pipefail

PORT="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(dirname "$PORT")"
MANAGED="$ROOT/Enter the Gungeon/EtG_Data/Managed"
PROJECT="$ROOT/ripped/ExportedProject"
REMOVED="$ROOT/ripped/_removed"

echo "== Building WebShim and Patcher"
dotnet build "$PORT/WebShim" -c Release -o "$PORT/build/WebShim" -nologo -v q
dotnet build "$PORT/Patcher" -c Release -o "$PORT/build/Patcher" -nologo -v q

echo "== Patching assemblies (originals are read from the game folder, never modified)"
mkdir -p "$PORT/build/patched"
rm -f "$PORT/build/patched/"*.dll   # clear contents only; the folder itself may be held open on Windows
dotnet "$PORT/build/Patcher/Patcher.dll" "$MANAGED" "$PORT/build/WebShim/WebShim.dll" "$PORT/build/patched"

echo "== Verifying every reference resolves against the WebGL player libraries"
UNITY_DATA="${UNITY_DATA:-/c/Program Files/Unity/Hub/Editor/2017.4.27f1/Editor/Data}"
if [ -d "$UNITY_DATA" ]; then
    X="$UNITY_DATA/UnityExtensions/Unity"
    dotnet "$PORT/build/Patcher/Patcher.dll" verify "$PORT/build/patched" \
        "$UNITY_DATA/PlaybackEngines/WebGLSupport/Managed" "$UNITY_DATA/Mono/lib/mono/unity" \
        "$X/GUISystem" "$X/Networking" "$X/Timeline/Runtime" "$X/UnitySpatialTracking/Runtime" \
        "$PROJECT/Assets/Plugins" | grep -v ": OK$"
else
    echo "   (Unity 2017.4.27f1 not found at $UNITY_DATA, skipping)"
fi

echo "== Installing into Unity project"
# The patcher renames Assembly-CSharp* (Unity ignores plugins with those reserved names).
# Move each old .meta onto the new file name so its GUID - which every scene/prefab
# script reference points at - stays the same.
PLUGINS="$PROJECT/Assets/Plugins"
for pair in "Assembly-CSharp-firstpass:EtgGameFirstpass" "Assembly-CSharp:EtgGame" "Assembly-UnityScript:EtgUnityScript"; do
    old="${pair%%:*}"; new="${pair##*:}"
    if [ -f "$PLUGINS/$old.dll.meta" ]; then
        echo "   $old.dll -> $new.dll (keeping GUID)"
        mv -f "$PLUGINS/$old.dll.meta" "$PLUGINS/$new.dll.meta"
    fi
    rm -f "$PLUGINS/$old.dll"
done
cp -f "$PORT/build/patched/"*.dll "$PLUGINS/"
cp -rf "$PORT/UnityAssets/." "$PROJECT/Assets/"

# Replace AssetRipper's dummy shaders with USCSandbox-decompiled ones (decompile_shaders.sh),
# matched by the name in `Shader "..."`. The .meta files - and with them every material's
# reference to the shader - stay untouched.
SHADERS_OUT="$ROOT/tools/uscs/out"
if [ -d "$SHADERS_OUT" ]; then
    declare -A decompiled
    while IFS= read -r -d '' f; do
        name=$(head -1 "$f" | sed -nE 's/^Shader "(.*)" \{.*/\1/p')
        [ -n "$name" ] && decompiled["$name"]="$f"
    done < <(find "$SHADERS_OUT" -name '*.shader' -print0)
    replaced=0; missing=0
    while IFS= read -r -d '' f; do
        name=$(head -1 "$f" | sed -nE 's/^Shader "(.*)" \{.*/\1/p')
        if [ -n "${decompiled[$name]:-}" ]; then
            if ! cmp -s "${decompiled[$name]}" "$f"; then
                cp -f "${decompiled[$name]}" "$f"; replaced=$((replaced + 1))
            fi
        elif grep -q "DummyShaderTextExporter" "$f"; then
            echo "   no decompiled shader for: $name"; missing=$((missing + 1))
        fi
    done < <(find "$PROJECT/Assets" -name '*.shader' -print0)
    echo "   shaders: $replaced updated, $missing dummies without a match"
else
    echo "   (no decompiled shaders in $SHADERS_OUT - run decompile_shaders.sh; keeping dummies)"
fi

# A few prefabs serialize assembly-qualified type names (PlayMaker variables).
grep -rlE 'Assembly-(CSharp|UnityScript)' "$PROJECT/Assets" --include=*.prefab --include=*.asset --include=*.unity 2>/dev/null |
while read -r f; do
    echo "   fixing assembly names in ${f#$PROJECT/}"
    sed -i -E 's/Assembly-CSharp-firstpass/EtgGameFirstpass/g; s/Assembly-CSharp([^-A-Za-z0-9_]|$)/EtgGame\1/g; s/Assembly-UnityScript([^-A-Za-z0-9_]|$)/EtgUnityScript\1/g' "$f"
done

# Windows AssetBundles can't be loaded by WebGL; Unity rebuilds them via "EtG Web/3".
# Wwise soundbanks are useless without the native Wwise engine (sound comes later).
mkdir -p "$REMOVED"
for d in Assets Audio; do
    src="$PROJECT/Assets/StreamingAssets/$d"
    if [ -d "$src" ] && [ ! -e "$REMOVED/StreamingAssets_$d" ]; then
        if [ "$d" = "Assets" ] && [ -f "$src/Assets" ]; then
            continue # already contains our WebGL build output (manifest bundle named "Assets")
        fi
        echo "   moving StreamingAssets/$d -> ripped/_removed/StreamingAssets_$d"
        mv "$src" "$REMOVED/StreamingAssets_$d"
        rm -f "$src.meta"
    fi
done

echo "== Done. Open $PROJECT in Unity 2017.4.27f1 and use the 'EtG Web' menu."
