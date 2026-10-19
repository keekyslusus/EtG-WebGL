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
# Our own mods (version "local" in modpack.txt) are built from port/mods/<pkg> into the same layout.
while read -r pkg ver; do
    if [ "$ver" = local ]; then
        rm -rf "$MODS/$pkg/plugins"
        dotnet build "$PORT/mods/$pkg" -c Release -o "$MODS/$pkg/plugins" -nologo -v q
    fi
done < <(grep -vE '^\s*(#|$)' "$PORT/mods/modpack.txt")
dirs=()
while read -r pkg ver; do
    if [ "$pkg" = "BepInEx-BepInExPack_EtG" ]; then
        dirs+=("$MODS/$pkg/BepInExPack_EtG/BepInEx/core")
    else
        dirs+=("$MODS/$pkg/plugins")
    fi
done < <(grep -vE '^\s*(#|$)' "$PORT/mods/modpack.txt")
mkdir -p "$PORT/build/modbaked"
rm -f "$PORT/build/modbaked/"*.dll   # contents only: the folder itself may be held open on Windows
"$PORT/build/ModBaker/ModBaker.exe" bake "$ROOT/Enter the Gungeon/EtG_Data/Managed" "$PORT/build/modbaked" "$PORT/build/ModFacades" "${dirs[@]}"
