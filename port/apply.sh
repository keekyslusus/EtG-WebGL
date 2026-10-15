#!/usr/bin/env bash
# Builds WebShim + Patcher, patches the game's original assemblies and installs
# everything into the AssetRipper-exported Unity project. Safe to re-run.
#   ./apply.sh          vanilla game (removes mods a previous --mods run installed)
#   ./apply.sh --mods   with the mods of mods/modpack.txt baked in (ModBaker, see README "Mods")
set -euo pipefail

MODS=0
if [ "${1:-}" = "--mods" ]; then
    MODS=1
fi

# Replacing the plugin DLLs under a running editor makes it hot-reload them, and that reload can leave its
# script domain broken (even Unity's own UnityEngine.UI / UnityEngine.Timeline stop resolving until restart).
if [ "${APPLY_WHILE_UNITY_OPEN:-0}" != 1 ] && tasklist //FI "IMAGENAME eq Unity.exe" //NH 2>/dev/null | grep -qi "^Unity.exe"; then
    echo "Unity is running - close it first (or APPLY_WHILE_UNITY_OPEN=1 to go ahead anyway)." >&2
    exit 1
fi

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
if [ "$MODS" = 1 ]; then
    echo "== Baking mods (mods/modpack.txt)"
    [ -d "$ROOT/mods" ] || bash "$PORT/mods/fetch_mods.sh"
    bash "$PORT/ModBaker/bake.sh"
    dotnet "$PORT/build/Patcher/Patcher.dll" "$MANAGED" "$PORT/build/WebShim/WebShim.dll" "$PORT/build/patched" "$PORT/build/modbaked"
else
    dotnet "$PORT/build/Patcher/Patcher.dll" "$MANAGED" "$PORT/build/WebShim/WebShim.dll" "$PORT/build/patched"
fi

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
# Mods and their libraries from a previous --mods run (a hidden file: Unity doesn't import it).
MOD_LIST="$PLUGINS/.mod-dlls"
if [ -f "$MOD_LIST" ]; then
    while read -r dll; do rm -f "$PLUGINS/$dll" "$PLUGINS/$dll.meta"; done < "$MOD_LIST"
    rm -f "$MOD_LIST"
fi
cp -f "$PORT/build/patched/"*.dll "$PLUGINS/"
MODS_ASSETS="$PROJECT/Assets/StreamingAssets/mods"
# Assets of the mods' own AssetBundles from a previous --mods run (mods/mod_bundles.py; with --mods it updates
# them in place, so Unity reimports only what changed).
if [ "$MODS" != 1 ] && [ -f "$PROJECT/Assets/.mod-assets" ]; then
    while read -r d; do [ -n "$d" ] && rm -rf "$PROJECT/Assets/$d" "$PROJECT/Assets/$d.meta"; done < "$PROJECT/Assets/.mod-assets"
    rm -f "$PROJECT/Assets/.mod-assets"
fi
if [ "$MODS" = 1 ]; then
    # Everything the patcher wrote that isn't one of the game's own assemblies.
    ( cd "$PORT/build/modbaked" && ls *.dll ) | grep -vxE 'Assembly-CSharp(-firstpass)?\.dll|Assembly-UnityScript\.dll|dfScriptLite\.dll|SonyNP\.dll|SonyPS4SavedGames\.dll|PlayMaker\.dll|ConditionalExpression\.dll|GalaxyCSharp\.dll' > "$MOD_LIST"
    echo "   mods: $(tr '\n' ' ' < "$MOD_LIST")"
    # Fixed GUIDs (md5 of "etgmod:<file>", as mods/mod_bundles.py computes them): the mods' prefabs in the
    # project reference their scripts through the DLL's GUID. Unity fills in the rest of the importer settings.
    while read -r dll; do
        printf 'fileFormatVersion: 2\nguid: %s\n' "$(printf 'etgmod:%s' "$dll" | md5sum | cut -c1-32)" > "$PLUGINS/$dll.meta"
    done < "$MOD_LIST"
    # The chainloader is found with Type.GetType - keep the mods whole through UnityLinker.
    mkdir -p "$PROJECT/Assets/EtgMods"
    { echo '<linker>'; sed -E 's/^(.*)\.dll$/  <assembly fullname="\1" preserve="all"\/>/' "$MOD_LIST"; echo '</linker>'; } > "$PROJECT/Assets/EtgMods/link.xml"
    echo "== Mod AssetBundle sources -> project (mods/mod_bundles.py)"
    python "$PORT/mods/mod_bundles.py" "$PORT/mods/modpack.txt" "$ROOT/mods" "$ROOT/tools/src" "$PROJECT/Assets" "$PORT/build/audio/mods"
    echo "== Mod sound banks -> webaudio/mods (audio/build_audio.py --mods)"
    python "$PORT/audio/build_audio.py" --mods "$PORT/build/audio/mods"
    echo "== Packing mod files"
    rm -rf "$PORT/build/il2cpp-resources"
    "$PORT/build/ModBaker/ModBaker.exe" resources "$PORT/build/patched" "$PORT/build/il2cpp-resources"
    python "$PORT/mods/build_pack.py" "$PORT/mods/modpack.txt" "$ROOT/mods" "$MODS_ASSETS/files.pack" "$ROOT/mods-runtime" "$PORT/build/il2cpp-resources"
    if [ -d "$ROOT/webbuild/StreamingAssets" ]; then
        # files.pack and the mods' videos (StreamingAssets/mods/video)
        rm -rf "$ROOT/webbuild/StreamingAssets/mods"
        mkdir -p "$ROOT/webbuild/StreamingAssets/mods"
        ( cd "$MODS_ASSETS" && find . -type f ! -name '*.meta' -exec cp --parents -f {} "$ROOT/webbuild/StreamingAssets/mods/" \; )
    fi
else
    rm -rf "$MODS_ASSETS" "$MODS_ASSETS.meta" "$PROJECT/Assets/EtgMods" "$PROJECT/Assets/EtgMods.meta" "$ROOT/webbuild/StreamingAssets/mods"
    for d in "$PROJECT/Assets/StreamingAssets/webaudio" "$ROOT/webbuild/StreamingAssets/webaudio"; do
        rm -rf "$d/mods" "$d/mods.meta" "$d/mods.json" "$d/mods.json.meta"
    done
fi
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
        [ -n "$name" ] || continue   # not an AssetRipper shader (e.g. our own Editor/EtgWebBuild/AbTestProbe.shader)
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

# Bundle assets AssetRipper exported under another name ("," -> "_", "_0" copies of Resources assets): a bundle finds
# an asset by its path or file name, and mods load the game's assets by their original names (bundle_names.py).
renamed=0
while IFS=$'\t' read -r from to; do
    case "$from" in ''|'#'*) continue ;; esac
    if [ -f "$PROJECT/$from" ]; then
        mkdir -p "$(dirname "$PROJECT/$to")"
        mv -f "$PROJECT/$from" "$PROJECT/$to"
        mv -f "$PROJECT/$from.meta" "$PROJECT/$to.meta"
        renamed=$((renamed + 1))
    fi
done < "$PORT/bundle_renames.tsv"
[ "$renamed" = 0 ] || echo "   bundle assets renamed to their original names: $renamed"

# Texture slots AssetRipper exported empty (the loading screen's throbber atlas).
python "$PORT/fix_materials.py" "$PORT/material_textures.tsv" "$PROJECT"

# A few prefabs serialize assembly-qualified type names (PlayMaker variables).
{ grep -rlE 'Assembly-(CSharp|UnityScript)' "$PROJECT/Assets" --include=*.prefab --include=*.asset --include=*.unity 2>/dev/null || true; } |
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
