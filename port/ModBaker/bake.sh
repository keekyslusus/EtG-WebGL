#!/usr/bin/env bash
# Bakes the mods of mods/modpack.txt (fetched into etg/mods by mods/fetch_mods.sh) into the game's
# assemblies: port/build/modbaked/*.dll (changed game DLLs + mod DLLs).
set -euo pipefail
PORT="$(cd "$(dirname "$0")/.." && pwd)"
ROOT="$(dirname "$PORT")"
MODS="$ROOT/mods"
dotnet build "$PORT/ModBaker" -c Release -o "$PORT/build/ModBaker" -nologo -v q
for p in BepInEx Harmony MonoMod.RuntimeDetour; do
    dotnet build "$PORT/ModFacades/$p" -c Release -o "$PORT/build/ModFacades" -nologo -v q
done
dirs=()
while read -r pkg ver; do
    if [ "$pkg" = "BepInEx-BepInExPack_EtG" ]; then
        dirs+=("$MODS/$pkg/BepInExPack_EtG/BepInEx/core")
    else
        dirs+=("$MODS/$pkg/plugins")
    fi
done < <(grep -vE '^\s*(#|$)' "$PORT/mods/modpack.txt")
rm -rf "$PORT/build/modbaked"
"$PORT/build/ModBaker/ModBaker.exe" bake "$ROOT/Enter the Gungeon/EtG_Data/Managed" "$PORT/build/modbaked" "$PORT/build/ModFacades" "${dirs[@]}"
