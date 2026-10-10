"""Post-processes USCSandbox output so it compiles for WebGL 2 (gles3) in Unity 2017.4.

Usage: python fix_shaders.py <dir with decompiled .shader files>   (edits files in place)

Fixes, all found by importing the decompiled shaders into Unity:
  * Light Probe Proxy Volume uniforms: the DX11 variants reference unity_ProbeVolume*, which
    UnityShaderVariables.cginc only declares when UNITY_LIGHT_PROBE_PROXY_VOLUME is 1 (never on
    gles3). Declared here under the inverse condition; on WebGL they stay 0 = feature disabled.
  * Meta pass uniforms (unity_MetaVertexControl & co.) come from UnityMetaPass.cginc, which the
    decompiled code doesn't include. Meta passes are only used for lightmap baking.
  * Loop counters: USCSandbox emits `i` (compared, incremented with `i = i + 1.0`) but never
    declares or initializes it. Declared at the top of the function and zeroed before each loop.
  * Built-in lighting keywords (LIGHTPROBE_SH, VERTEXLIGHT_ON, ...) emitted as shader_feature:
    builds strip those variants, so they become multi_compile.
  * Single-shader decompiler slips, patched by exact text.
"""
import re
import sys
from pathlib import Path

MARK = "// [EtG WebGL] "

PROBE_VOLUME = [
    ("unity_ProbeVolumeParams", "float4"),
    ("unity_ProbeVolumeWorldToObject", "float4x4"),
    ("unity_ProbeVolumeSizeInv", "float3"),
    ("unity_ProbeVolumeMin", "float3"),
]

META = [
    ("unity_MetaVertexControl", "bool4"),
    ("unity_MetaFragmentControl", "bool4"),
    ("unity_OneOverOutputBoost", "float"),
    ("unity_MaxOutputValue", "float"),
    ("unity_UseLinearSpace", "float"),
    ("unity_VisualizationMode", "int"),
]

# (shader name, wrong text, right text)
EXACT_FIXES = [
    # float4 = float4 * float4 + float2: the DXBC operand was v1.xyxy
    ("Brave/Effects/Starnest_Replacement_Cheap",
     "* float4(0.1666667, 0.05, 0.1666667, 0.05) + inp.texcoord.xy;",
     "* float4(0.1666667, 0.05, 0.1666667, 0.05) + inp.texcoord.xyxy;"),
]


def declared(block: str, name: str) -> bool:
    # a real (uncommented) declaration like "float4 name;" or "bool4 name;"
    return re.search(r"^(?!\s*//).*\b\w+\s+" + re.escape(name) + r"\s*;", block, re.M) is not None


def uses(block: str, name: str) -> bool:
    return re.search(r"^(?!\s*//).*\b" + re.escape(name) + r"\b", block, re.M) is not None


def fix_uniforms(block: str) -> str:
    lines = []
    probe = [(n, t) for n, t in PROBE_VOLUME if not declared(block, n)]
    if any(uses(block, n) for n, _ in probe) or uses(block, "unity_ProbeVolumeSH"):
        lines.append("#if !UNITY_LIGHT_PROBE_PROXY_VOLUME")
        lines += [f"{t} {n};" for n, t in probe]
        # sampled through UNITY_SAMPLE_TEX3D_SAMPLER; declared the same way UnityShaderVariables does
        if uses(block, "unity_ProbeVolumeSH") and "UNITY_DECLARE_TEX3D_FLOAT(unity_ProbeVolumeSH)" not in block:
            lines.append("UNITY_DECLARE_TEX3D_FLOAT(unity_ProbeVolumeSH);")
        lines.append("#endif")
    lines += [f"{t} {n};" for n, t in META if uses(block, n) and not declared(block, n)]
    if not lines:
        return block
    header = "\n" + MARK + "uniforms missing on gles3\n" + "\n".join(lines) + "\n"
    # right after the #pragma lines at the top of the program
    m = re.search(r"((?:^[ \t]*#pragma[^\n]*\n)+)", block, re.M)
    pos = m.end() if m else 0
    return block[:pos] + header + block[pos:]


FUNC_RE = re.compile(r"^(\s*)\w+\s+(\w+)\s*\([^)]*\)\s*\n\s*\{", re.M)


def body_end(text: str, start: int) -> int:
    """Index just past the '}' closing the block whose '{' precedes start."""
    depth, i = 1, start
    while depth and i < len(text):
        depth += {"{": 1, "}": -1}.get(text[i], 0)
        i += 1
    return i


LOOP_RE = re.compile(r"^(\s*)while \(true\) \{", re.M)
USES_I = re.compile(r"\bi\b")
REG_INCREMENT = re.compile(r"\b(tmp\d+\.[xyzw])\s*=\s*\1\s*\+\s*1(?:\.0)?\s*;")


def fix_loops_in_function(body: str, indent: str) -> str:
    """USCSandbox names a loop counter `i` but forgets to declare it. Two shapes occur:
      a) `i = i + 1.0;` inside the loop: declare `i` and zero it before the loop;
      b) the real counter is a register (`tmp2.x = tmp2.x + 1;`, zeroed before the loop) and
         only the condition/indexing say `i`: use that register instead."""
    out, last, declare = [], 0, False
    for m in LOOP_RE.finditer(body):
        end = body_end(body, m.end())
        loop = body[m.start():end]
        if not USES_I.search(loop) or re.search(r"\b(float|int|uint)\s+i\b", body):
            continue
        out.append(body[last:m.start()])
        if re.search(r"\bi\s*=\s*i\s*\+", loop):
            out.append(f"{m.group(1)}i = 0.0; {MARK}loop counter\n{loop}")
            declare = True
        else:
            reg = REG_INCREMENT.search(loop)
            if not reg:
                raise ValueError("loop uses undeclared `i` without a recognizable counter:\n" + loop[:400])
            fixed = USES_I.sub(f"((int){reg.group(1)})", loop)
            out.append(f"{m.group(1)}{MARK}loop counter `i` is {reg.group(1)}\n{fixed}")
        last = end
    out.append(body[last:])
    body = "".join(out)
    if declare:
        body = f"\n{indent}float i; {MARK}loop counter declared by the fixer" + body
    return body


def fix_loop_counters(block: str) -> str:
    out, last = [], 0
    for m in FUNC_RE.finditer(block):
        out.append(block[last:m.start()])
        start = m.end()
        end = body_end(block, start)
        out.append(block[m.start():start] + fix_loops_in_function(block[start:end], m.group(1) + "    "))
        last = end
    out.append(block[last:])
    return "".join(out)


# Keywords Unity's forward renderer sets per draw call (never stored on materials).
BUILTIN_KEYWORDS = ["LIGHTPROBE_SH", "VERTEXLIGHT_ON", "SHADOWS_SCREEN", "POINT", "SPOT",
                    "SOFTPARTICLES_ON", "UNITY_HDR_ON", "SHADOWS_DEPTH", "SHADOWS_CUBE", "SHADOWS_SOFT"]
SHADER_FEATURE_RE = re.compile(
    r"^([ \t]*)#pragma shader_feature (" + "|".join(BUILTIN_KEYWORDS) + r")[ \t]*$", re.M)


def fix_builtin_keywords(text: str) -> str:
    # USCSandbox writes every single-keyword set as shader_feature. Builds strip shader_feature
    # variants no material enables, and nothing enables these on a material - so bundles kept only
    # the bare DIRECTIONAL variants (no ambient SH, no vertex lights) and the WebGL build rendered
    # nearly black, while the editor, compiling variants on demand, looked fine.
    return SHADER_FEATURE_RE.sub(
        lambda m: f"{m.group(1)}{MARK}builtin keyword: shader_feature would strip it from builds\n"
                  f"{m.group(1)}#pragma multi_compile __ {m.group(2)}", text)


def fix_shader(text: str) -> str:
    already_fixed = MARK in text
    text = fix_builtin_keywords(text)  # idempotent on its own, also for previously fixed files
    if already_fixed:
        return text
    name = re.match(r'Shader "([^"]+)"', text)
    for shader, wrong, right in EXACT_FIXES:
        if name and name.group(1) == shader:
            text = text.replace(wrong, right)
    parts = re.split(r"(CGPROGRAM.*?ENDCG)", text, flags=re.S)
    for k in range(1, len(parts), 2):
        parts[k] = fix_loop_counters(fix_uniforms(parts[k]))
    return "".join(parts)


def main(root: str) -> None:
    changed = 0
    files = sorted(Path(root).rglob("*.shader"))
    for f in files:
        before = f.read_text(encoding="utf-8")
        after = fix_shader(before)
        if after != before:
            f.write_text(after, encoding="utf-8", newline="\n")
            changed += 1
    print(f"fix_shaders: {changed} of {len(files)} shaders patched")


if __name__ == "__main__":
    main(sys.argv[1])
