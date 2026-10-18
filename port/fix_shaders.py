"""Post-processes USCSandbox output so it compiles for WebGL 2 (gles3) in Unity 6.6 (found on Unity 2017.4).

Usage: python fix_shaders.py <dir with decompiled .shader files>   (edits files in place)
The result is meant to be right on both D3D11 and GLES: GLES-specific rewrites sit under #if on the platform.

Fixes, all found by importing the decompiled shaders into Unity:
  * Light Probe Proxy Volume uniforms: the DX11 variants reference unity_ProbeVolume*, which
    UnityShaderVariables.cginc only declares when UNITY_LIGHT_PROBE_PROXY_VOLUME is 1 (never on
    gles3 in 2017.4). Declared here under the inverse condition; on WebGL they stay 0 = feature disabled. Unity 6.6
    defines UNITY_LIGHT_PROBE_PROXY_VOLUME for WebGL (EtgAbTest's webglDefines), so there this is inactive.
  * Meta pass uniforms (unity_MetaVertexControl & co.) come from UnityMetaPass.cginc, which the
    decompiled code doesn't include. Meta passes are only used for lightmap baking.
  * Loop counters: USCSandbox emits `i` (compared, incremented with `i = i + 1.0`) but never
    declares or initializes it. Declared at the top of the function and zeroed before each loop.
  * Built-in lighting keywords (LIGHTPROBE_SH, VERTEXLIGHT_ON, ...) emitted as shader_feature:
    builds strip those variants, so they become multi_compile.
  * sign() on DXBC comparison masks (true = -1) came out negated: "b - a" of two raw comparisons
    is swapped back. It made every Pixelator light subtract.
  * Baked occlusion: saturate(dot(1111, unity_OcclusionMaskSelector)) from the LPPV DX11 variants is 1.0
    without LPPV (gles3 in 2017.4; the engine leaves the selector at zero there); it zeroed every Unity light.
    Inactive with 6.6's LPPV on WebGL, where the original code is right (abtest: lit renders equal D3D11).
  * Built-in tags come back upper-cased ("LIGHTMODE"="FORWARDADD"); Unity does not recognise those LightMode
    values when compiling from source, so no Unity light ever lit a sprite. Restored to Unity spelling.
  * Light attenuation textures (_LightTexture0/B0) are read from .x as on D3D11; on GLES Unity stores them
    in alpha (UNITY_ATTEN_CHANNEL a), so every Unity light was attenuated to zero. Swizzle by UNITY_ATTEN_CHANNEL.
  * D3D renders into textures with a flipped projection; screen positions the game derives from the clip position
    are computed in D3D's clip space on GLES (title screen water was upside down).
  * ShadowCaster passes: D3D's reversed-Z shadow bias gets the GL variant on GLES.
  * "ZTest Unknown": a pass with the depth test disabled (CompareFunction.Disabled, 0) has no ShaderLab spelling;
    Unity 6.6 fails the whole shader on it (Brave/CameraEffects/DepthToColor). Disabled = Always.
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
# (Starnest_Replacement_Cheap's "+ inp.texcoord.xy" used to be patched here to ".xyxy"; that was wrong too: the
# DXBC operand was v0.xyzw = texcoord.xy + texcoord1.xy, now decompiled correctly by USCSandbox.)
EXACT_FIXES = []


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
BOOL_SUB_RE = re.compile(r"(tmp\d+)\.([xyzw]+) - (tmp\d+)\.([xyzw]+)")
BOOL_SUB_MARK = MARK + "bool subtraction"


def fix_bool_subtraction(text: str) -> str:
    # DXBC comparisons yield -1 (all bits set) for true, and sign() compiles to
    # iadd(-(0 < x), (x < 0)) on those masks. USCSandbox turns comparisons into 1.0/0.0 but keeps
    # the subtraction order, so "b - a" on two raw comparison results is -sign(x): the light
    # shaders' final "* sign(_LightIntensity)" made every Pixelator light subtract (black blobs).
    # Also per vector ("tmp0.yz = tmp0.yz - tmp1.xy", SimpleCameraMask's minimap border).
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
        if sub:
            a_reg, a_sw, b_reg, b_sw = sub.groups()
            if len(a_sw) == len(b_sw) == len(comps) and all(f"{a_reg}.{c}" in raw_bool for c in a_sw) \
                    and all(f"{b_reg}.{c}" in raw_bool for c in b_sw):
                lines[i] = f"{indent}{reg}.{comps} = {b_reg}.{b_sw} - {a_reg}.{a_sw}; {BOOL_SUB_MARK}"
        is_bool = "?" not in expr and COMPARISON_RE.search(expr) is not None
        for c in comps:
            (raw_bool.add if is_bool else raw_bool.discard)(f"{reg}.{c}")
    return "\n".join(lines)


OCCLUSION_SELECTOR_RE = re.compile(
    r"^(\s*)(tmp\d+\.[xyzw]) = saturate\(dot\(tmp\d+, unity_OcclusionMaskSelector\)\);[ \t]*$", re.M)
OCCLUSION_MARK = MARK + "baked occlusion"


def fix_occlusion_selector(text: str) -> str:
    # The DX11 variants were compiled with UNITY_LIGHT_PROBE_PROXY_VOLUME, where
    # UnitySampleBakedOcclusion returns saturate(dot(lppvOcclusionOr1111, unity_OcclusionMaskSelector)).
    # Without LPPV (gles3) Unity's own code returns 1.0 there and the engine leaves the selector at zero, so
    # every Unity light (spot/point/directional) came out black. No EtG variant uses a shadowmask.
    if OCCLUSION_MARK in text:
        return text
    return OCCLUSION_SELECTOR_RE.sub(lambda m: (
        f"{m.group(1)}#if UNITY_LIGHT_PROBE_PROXY_VOLUME {OCCLUSION_MARK}: no LPPV/selector on gles3\n"
        f"{m.group(0).rstrip()}\n"
        f"{m.group(1)}#else\n"
        f"{m.group(1)}{m.group(2)} = 1.0;\n"
        f"{m.group(1)}#endif"), text)


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
ATTEN_WRAP = "ETG_ATTEN("
# fxc doesn't expand a function-like macro right after '.', hence the wrapper around the whole read
ATTEN_MACROS = (MARK + "light attenuation channel: UNITY_ATTEN_CHANNEL is r on D3D11 (identity), a on GLES\n"
                "#define ETG_ATTEN_SWIZZLE_r xyzw\n"
                "#define ETG_ATTEN_SWIZZLE_a wwww\n"
                "#define ETG_ATTEN__(x, c) (x).ETG_ATTEN_SWIZZLE_##c\n"
                "#define ETG_ATTEN_(x, c) ETG_ATTEN__(x, c)\n"
                "#define ETG_ATTEN(x) ETG_ATTEN_(x, UNITY_ATTEN_CHANNEL)\n")


def fix_atten_channel(program: str) -> str:
    # Unity's light attenuation lookups use UNITY_ATTEN_CHANNEL: r on D3D11, a on GLES, where these
    # are Alpha8 textures (r reads 0). The DX11 code reads .x, so every Unity light (point/spot)
    # was attenuated to zero on gles3 - the ForwardAdd passes ran but added nothing. Read the whole
    # texel through a swizzle that follows UNITY_ATTEN_CHANNEL: .xyzw (unchanged) on D3D11, .wwww on GLES.
    # Cookies are read from .w, the same on both. One CGPROGRAM block at a time (the macros go into each).
    if ATTEN_WRAP in program:
        return program

    def repl(m):
        if m.group(2):  # the decompiler never emits one; a swizzle here would need its own macro
            raise ValueError("swizzled attenuation texture read: " + m.group(0))
        return ATTEN_WRAP + m.group(1) + ")"
    fixed = ATTEN_SAMPLE_RE.sub(repl, program)
    if fixed == program:
        return program
    m = re.search(r"((?:^[ \t]*#pragma[^\n]*\n)+)", fixed, re.M)
    pos = m.end() if m else 0
    return fixed[:pos] + "\n" + ATTEN_MACROS + fixed[pos:]


VERT_RE = re.compile(r"^\s*v2f vert\(appdata v\)\s*\{", re.M)
CLIP_FLIP_MARK = MARK + "D3D clip space"


def fix_clip_space_flip(text: str) -> str:
    # D3D renders into textures with a vertically flipped projection, GL doesn't; the game cameras render into
    # textures (Pixelator), and the game's own vertex code relies on that: it derives screen positions straight from
    # the clip position (title screen water: (clip + 1) / 2, its "y > 0.92" cutoff and sky gradient came out upside
    # down - white water), multiplies by (1, -1) to undo the flip (PartialDesaturationEffect), or has
    # ComputeGrabScreenPos' D3D -1 baked in. On GLES, compute those in D3D's flipped clip space and flip only what
    # goes to the rasterizer back. Not when the code uses _ProjectionParams.x (ComputeScreenPos handles the flip).
    out, last = [], 0
    for m in VERT_RE.finditer(text):
        start = m.end()
        end = body_end(text, start)
        body = text[start:end]
        if CLIP_FLIP_MARK in body or "_ProjectionParams" in body:
            continue
        lines = body.split("\n")
        for i, line in enumerate(lines):
            pm = re.match(r"^(\s*)o\.(position|sv_position) = (tmp\d+)\b.*;\s*$", line)
            if not pm:
                continue
            indent, out_name, reg = pm.groups()
            if not any(re.search(r"\b" + reg + r"\b", l) for l in lines[i + 1:]):
                break  # nothing else derived from the clip position
            lines[i] = (f"{indent}#if !UNITY_UV_STARTS_AT_TOP {CLIP_FLIP_MARK}\n"
                        f"{indent}{reg}.y = -{reg}.y;\n"
                        f"{indent}#endif\n"
                        f"{line}\n"
                        f"{indent}#if !UNITY_UV_STARTS_AT_TOP\n"
                        f"{indent}o.{out_name}.y = -o.{out_name}.y;\n"
                        f"{indent}#endif")
            break
        out.append(text[last:start] + "\n".join(lines))
        last = end
    out.append(text[last:])
    return "".join(out)


SHADOW_BIAS_RE = re.compile(
    r"^(\s*)(tmp\d+\.[xyzw]) = unity_LightShadowBias\.x / (tmp\d+)\.w;\n"
    r"\s*\2 = min\(\2, 0\.0\);\n"
    r"\s*\2 = max\(\2, -1\.0\);\n", re.M)
SHADOW_CLAMP_RE = re.compile(r"^(\s*)(tmp\d+\.[xyzw]) = min\((tmp\d+)\.w, \3\.z\);[ \t]*$", re.M)
SHADOW_MARK = MARK + "reversed-Z shadow bias"


def fix_shadow_bias(text: str) -> str:
    # UnityApplyLinearShadowBias (ShadowCaster passes) compiled for D3D's reversed Z: the bias is clamped to [-1, 0]
    # and z to min(z, w). With GL's depth range it is saturate(bias / w) and max(z, -w); as decompiled, GL got no
    # depth bias at all. Only Unity's own shadow maps use it.
    if SHADOW_MARK in text or "unity_LightShadowBias" not in text:
        return text
    text = SHADOW_BIAS_RE.sub(lambda m: (
        f"{m.group(1)}{m.group(2)} = unity_LightShadowBias.x / {m.group(3)}.w; {SHADOW_MARK}\n"
        f"{m.group(1)}#if UNITY_REVERSED_Z\n"
        f"{m.group(1)}{m.group(2)} = min({m.group(2)}, 0.0);\n"
        f"{m.group(1)}{m.group(2)} = max({m.group(2)}, -1.0);\n"
        f"{m.group(1)}#else\n"
        f"{m.group(1)}{m.group(2)} = saturate({m.group(2)});\n"
        f"{m.group(1)}#endif\n"), text)
    return SHADOW_CLAMP_RE.sub(lambda m: (
        f"{m.group(1)}#if UNITY_REVERSED_Z {SHADOW_MARK}\n"
        f"{m.group(0).rstrip()}\n"
        f"{m.group(1)}#else\n"
        f"{m.group(1)}{m.group(2)} = max(-{m.group(3)}.w, {m.group(3)}.z);\n"
        f"{m.group(1)}#endif"), text)


ZTEST_UNKNOWN_RE = re.compile(r"^(\s*ZTest) Unknown\b", re.M)


def fix_ztest_unknown(text: str) -> str:
    return ZTEST_UNKNOWN_RE.sub(r"\1 Always", text)


def fix_shader(text: str) -> str:
    # The DX11-vs-GLES rewrites (attenuation channel, baked occlusion, clip space flip, shadow bias) are all under
    # #if/macros on the platform, no-ops on D3D11: shaderdiff.sh checks they don't change the DX11 result, and the
    # same files are right in the editor on D3D11 and GLES (port/abtest.sh compares the two).
    text = fix_tag_case(text)  # idempotent on its own
    text = fix_ztest_unknown(text)  # idempotent on its own
    already_fixed = MARK in text
    text = fix_occlusion_selector(text)  # idempotent on its own, also for previously fixed files
    text = fix_builtin_keywords(text)  # idempotent on its own, also for previously fixed files
    text = fix_clip_space_flip(text)  # idempotent on its own
    text = fix_shadow_bias(text)  # idempotent on its own
    if BOOL_SUB_MARK not in text:
        text = fix_bool_subtraction(text)
    parts = re.split(r"(CGPROGRAM.*?ENDCG)", text, flags=re.S)
    for k in range(1, len(parts), 2):
        parts[k] = fix_atten_channel(parts[k])  # idempotent on its own
    text = "".join(parts)
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
