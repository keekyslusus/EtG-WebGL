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
  * sign() on DXBC comparison masks (true = -1) came out negated: "b - a" of two raw comparisons
    is swapped back. It made every Pixelator light subtract.
  * Baked occlusion: saturate(dot(1111, unity_OcclusionMaskSelector)) from the LPPV DX11 variants is 1.0
    on gles3 (the engine leaves the selector at zero there); it zeroed every Unity light.
  * Built-in tags come back upper-cased ("LIGHTMODE"="FORWARDADD"); Unity does not recognise those LightMode
    values when compiling from source, so no Unity light ever lit a sprite. Restored to Unity spelling.
  * Light attenuation textures (_LightTexture0/B0) are read from .x as on D3D11; on GLES Unity stores them
    in alpha (UNITY_ATTEN_CHANNEL a), so every Unity light was attenuated to zero. Read .w.
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
     "* float4(0.16666667, 0.05, 0.16666667, 0.05) + inp.texcoord.xy;",
     "* float4(0.16666667, 0.05, 0.16666667, 0.05) + inp.texcoord.xyxy;"),
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


ASSIGN_RE = re.compile(r"^(\s*)(tmp\d+)\.([xyzw]+) = (.*);\s*$")
COMPARISON_RE = re.compile(r"\s(<|>|<=|>=|==|!=)\s")
BOOL_SUB_RE = re.compile(r"(tmp\d+\.[xyzw]) - (tmp\d+\.[xyzw])")
BOOL_SUB_MARK = MARK + "bool subtraction"


def fix_bool_subtraction(text: str) -> str:
    # DXBC comparisons yield -1 (all bits set) for true, and sign() compiles to
    # iadd(-(0 < x), (x < 0)) on those masks. USCSandbox turns comparisons into 1.0/0.0 but keeps
    # the subtraction order, so "b - a" on two raw comparison results is -sign(x): the light
    # shaders' final "* sign(_LightIntensity)" made every Pixelator light subtract (black blobs).
    lines = text.split("\n")
    raw_bool = set()
    for i, line in enumerate(lines):
        if re.match(r"^\s*(fout|v2f) \w+\(", line):
            raw_bool = set()
        m = ASSIGN_RE.match(line)
        if not m:
            continue
        indent, reg, comps, expr = m.groups()
        sub = BOOL_SUB_RE.fullmatch(expr.strip())
        if sub and sub.group(1) in raw_bool and sub.group(2) in raw_bool:
            lines[i] = f"{indent}{reg}.{comps} = {sub.group(2)} - {sub.group(1)}; {BOOL_SUB_MARK}"
        is_bool = len(comps) == 1 and "?" not in expr and COMPARISON_RE.search(expr) is not None
        for c in comps:
            (raw_bool.add if is_bool else raw_bool.discard)(f"{reg}.{c}")
    return "\n".join(lines)


OCCLUSION_SELECTOR_RE = re.compile(
    r"^(\s*)(tmp\d+\.[xyzw]) = saturate\(dot\(tmp\d+, unity_OcclusionMaskSelector\)\);[ \t]*$", re.M)


def fix_occlusion_selector(text: str) -> str:
    # The DX11 variants were compiled with UNITY_LIGHT_PROBE_PROXY_VOLUME, where
    # UnitySampleBakedOcclusion returns saturate(dot(lppvOcclusionOr1111, unity_OcclusionMaskSelector)).
    # On gles3 Unity's own code returns 1.0 there and the engine leaves the selector at zero, so
    # every Unity light (spot/point/directional) came out black. No EtG variant uses a shadowmask.
    return OCCLUSION_SELECTOR_RE.sub(
        lambda m: f"{m.group(1)}{m.group(2)} = 1.0; {MARK}baked occlusion: no shadowmask on gles3", text)


# Compiled shaders store built-in tags upper-cased ("LIGHTMODE"="FORWARDADD"); USCSandbox writes them
# back like that. Unity's ShaderLab parser doesn't recognise the upper-case LightMode values, so the
# ForwardBase/ForwardAdd passes ran as plain passes without light data: no Unity light (spot, point,
# vertex) ever reached a sprite. Restore the spelling Unity's own sources use.
TAG_NAMES = {"LIGHTMODE": "LightMode", "QUEUE": "Queue", "IGNOREPROJECTOR": "IgnoreProjector",
             "SHADOWSUPPORT": "ShadowSupport", "RENDERTYPE": "RenderType", "FORCENOSHADOWCASTING": "ForceNoShadowCasting",
             "DISABLEBATCHING": "DisableBatching", "CANUSESPRITEATLAS": "CanUseSpriteAtlas", "PREVIEWTYPE": "PreviewType"}
LIGHTMODES = {"FORWARDBASE": "ForwardBase", "FORWARDADD": "ForwardAdd", "META": "Meta", "SHADOWCASTER": "ShadowCaster",
              "ALWAYS": "Always", "DEFERRED": "Deferred", "PREPASSBASE": "PrepassBase", "PREPASSFINAL": "PrepassFinal",
              "VERTEX": "Vertex", "VERTEXLMRGBM": "VertexLMRGBM", "VERTEXLM": "VertexLM", "MOTIONVECTORS": "MotionVectors"}
TAG_RE = re.compile(r'"([A-Z]+)"="([^"]*)"')


def fix_tag_case(text: str) -> str:
    def repl(m):
        name, value = m.group(1), m.group(2)
        if name not in TAG_NAMES:
            return m.group(0)
        if name == "LIGHTMODE":
            value = LIGHTMODES.get(value, value)
        return f'"{TAG_NAMES[name]}"="{value}"'
    return TAG_RE.sub(repl, text)


ATTEN_SAMPLE_RE = re.compile(r"(tex2D\((?:_LightTexture0|_LightTextureB0|unity_NHxRoughness), [^()]*\))(\.[xyzw]+)?")


def fix_atten_channel(text: str) -> str:
    # Unity's light attenuation lookups use UNITY_ATTEN_CHANNEL: r on D3D11, a on GLES, where these
    # are Alpha8 textures (r reads 0). The DX11 code reads .x, so every Unity light (point/spot)
    # was attenuated to zero on gles3 - the ForwardAdd passes ran but added nothing. Read alpha.
    # (Cookies are read from .w already, so this is a no-op for them; the rewrite is idempotent.)
    def repl(m):
        swizzle = m.group(2)
        return m.group(1) + ("." + "w" * (len(swizzle) - 1) if swizzle else ".wwww")
    return ATTEN_SAMPLE_RE.sub(repl, text)


def fix_shader(text: str) -> str:
    text = fix_tag_case(text)  # idempotent on its own
    text = fix_atten_channel(text)  # idempotent on its own
    already_fixed = MARK in text
    text = fix_occlusion_selector(text)  # idempotent on its own, also for previously fixed files
    text = fix_builtin_keywords(text)  # idempotent on its own, also for previously fixed files
    if BOOL_SUB_MARK not in text:
        text = fix_bool_subtraction(text)
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
