"""Turns off the "Remove duplicate asm.js code" step of Unity 2017.4's WebGL release builds.

    python port/patch_webgl_dedupe.py <UnityEditor.WebGL.Extensions.dll> [<output>]

Release (non-development) builds run CodeAnalysisUtils.ReplaceDuplicates on the asm.js fallback, which
2017.4 always links next to the wasm build. It reads the whole asm.js into one string: EtG's is ~800 MB
(the JS optimizer is off, see ApplyPlayerSettings' --js-opts 0), and the editor's Mono throws
OutOfMemoryException - the release build fails while development builds, which skip the step, work.
The browser page runs the wasm build anyway (WebGL 2 only).

WebGlBuildPostprocessor.EmscriptenLink starts with
    bool dedupe = !wasmBuild && !m_DevelopmentPlayer;
    // nop; ldarg.2; brtrue L; ldarg.0; ldfld m_DevelopmentPlayer; ldc.i4.0; ceq; br.s M; L: ldc.i4.0; M: stloc.0
The patch turns `ldarg.2; brtrue L` into `nop; br L` (the nop keeps the stack balanced), so dedupe is always
false. The step's only other output, the stripped symbols, is read only with Debug Symbols on
(PlayerSettings.WebGL.debugSymbols, off here).

Without <output> the DLL is patched in place (needs admin under Program Files; close Unity first) and the
original kept as .orig. The DLL lives in <Unity>/Editor/Data/PlaybackEngines/WebGLSupport/.
"""
import os
import re
import shutil
import sys

PROLOGUE = rb"\x00(\x04)(\x3a).{4}\x02\x7b.{4}\x16\xfe\x01\x2b\x01\x16\x0a"
PATCHED = rb"\x00\x00\x38.{4}\x02\x7b.{4}\x16\xfe\x01\x2b\x01\x16\x0a"


def main():
    if len(sys.argv) < 2:
        raise SystemExit(__doc__)
    src = sys.argv[1]
    dst = sys.argv[2] if len(sys.argv) > 2 else src
    data = bytearray(open(src, "rb").read())
    matches = list(re.finditer(PROLOGUE, data, re.S))
    if not matches and len(re.findall(PATCHED, data, re.S)) == 1:
        print("already patched:", src)
        return
    if len(matches) != 1:
        raise SystemExit(f"expected the EmscriptenLink prologue once, found it {len(matches)} times - not 2017.4.27f1's DLL?")
    data[matches[0].start(1)] = 0x00  # ldarg.2 -> nop
    data[matches[0].start(2)] = 0x38  # brtrue -> br, same int32 target
    if dst == src:
        backup = src + ".orig"
        if not os.path.exists(backup):
            shutil.copy2(src, backup)
    with open(dst, "wb") as f:
        f.write(data)
    print("patched:", dst)


if __name__ == "__main__":
    main()
