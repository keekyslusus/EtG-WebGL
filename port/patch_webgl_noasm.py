"""Stops Unity 2017.4's WebGL builds from linking and packaging the asm.js fallback next to the wasm build.

    python port/patch_webgl_noasm.py <UnityEditor.WebGL.Extensions.dll> [<output>]

2017.4 always builds both: WebGlBuildPostprocessor.LinkBuild runs a whole extra Emscripten link for asm.js
(`if (m_UseAsm) EmscriptenLink(args, false, ...)`) before the wasm one, and AssembleOutput packs the result
(`if (m_UseAsm) { AssembleAsmCode(...) ... }`). The page only runs wasm (WebGL 2), and EtgWebBuild deleted the
asm.js files after every build anyway. With the mods baked in, the development build's asm.js is 3.45 GB and
AssembleAsmCode's File.ReadAllBytes throws ("Reading more than 2GB with this call is not supported"): the build
fails after two hours. The wasm link doesn't use the asm.js link's output (it links its own asm.js for asm2wasm).

The patch turns both `ldarg.0; ldfld m_UseAsm` into `ldc.i4.0; nop x5`, so both `brfalse` always skip.
Without <output> the DLL is patched in place (needs admin under Program Files; close Unity first) and the
original is kept as .orig unless a .orig exists already (patch_webgl_dedupe.py makes one). The DLL lives in
<Unity>/Editor/Data/PlaybackEngines/WebGLSupport/.
"""
import re
import shutil
import sys
import os

# ldarg.0; ldfld <WebGlBuildPostprocessor::m_UseAsm>; brfalse <int32>
LOAD = rb"\x02\x7b(\x40\x00\x00\x04)\x39"
PATCHED = b"\x16\x00\x00\x00\x00\x00\x39"


def main():
    if len(sys.argv) < 2:
        raise SystemExit(__doc__)
    src = sys.argv[1]
    dst = sys.argv[2] if len(sys.argv) > 2 else src
    data = bytearray(open(src, "rb").read())
    matches = list(re.finditer(LOAD, data, re.S))
    if not matches and len(re.findall(re.escape(PATCHED), data)) >= 2:
        print("already patched:", src)
        return
    # The token is m_UseAsm in the 2017.4.27f1 DLL (two branches: LinkBuild, AssembleOutput); anything else
    # means another Unity version: check the IL (ilspycmd -il) before changing the pattern.
    if len(matches) != 2:
        raise SystemExit(f"expected 2 `if (m_UseAsm)` branches, found {len(matches)} - another Unity version?")
    for m in matches:
        data[m.start():m.start() + len(PATCHED)] = PATCHED
    if dst == src and not os.path.exists(src + ".orig"):
        shutil.copyfile(src, src + ".orig")
    with open(dst, "wb") as f:
        f.write(data)
    print(f"patched {len(matches)} branches:", dst)


if __name__ == "__main__":
    main()
