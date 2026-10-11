"""Audit installed Unity shader selection against the original DXBC variant inventory.

No third-party Python packages. See port/README.md for the scope of the oracle.
The empirical run deliberately replaces program bodies, never the original assets.
"""
import argparse
from collections import Counter, defaultdict
from dataclasses import dataclass
import hashlib
from functools import lru_cache
import itertools
import json
import math
from pathlib import Path
import re
import shutil
import subprocess

ROOT = Path(__file__).resolve().parent.parent
TEMP = "Assets/Editor/EtgWebBuild/_variants"
ENGINE = set("DIRECTIONAL POINT SPOT DIRECTIONAL_COOKIE POINT_COOKIE LIGHTPROBE_SH VERTEXLIGHT_ON "
             "SHADOWS_SCREEN SHADOWS_DEPTH SHADOWS_CUBE SHADOWS_SOFT SHADOWS_NATIVE "
             "LIGHTMAP_ON DIRLIGHTMAP_COMBINED DYNAMICLIGHTMAP_ON FOG_LINEAR FOG_EXP FOG_EXP2 "
             "INSTANCING_ON UNITY_HDR_ON STEREO_MULTIVIEW_ON STEREO_INSTANCING_ON".split())
SETUPS = {
    "dark": {"DIRECTIONAL"}, "dir": {"DIRECTIONAL"}, "point": {"POINT"}, "spot": {"SPOT"},
    "directional_cookie": {"DIRECTIONAL_COOKIE"}, "point_cookie": {"POINT_COOKIE"},
    "spot_cookie": {"SPOT"}, "vertex": {"DIRECTIONAL", "VERTEXLIGHT_ON"},
    "probe": {"DIRECTIONAL", "LIGHTPROBE_SH"},
    "shadows": {"DIRECTIONAL", "SHADOWS_SCREEN"},
}
SENSITIVITY_SHADERS = {"LitCutoutUber", "LitBlendUber", "LitCutoutUber_ColorEmissive", "BurnableSprite", "DisplacerBeast"}


def read(path):
    return Path(path).read_text(encoding="utf-8-sig")


def write_json(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def snapshot(shaders, materials, project, dump):
    paths = {s["path"] for s in shaders.values()}
    paths.update(project / m["path"] for m in materials)
    paths.update(Path(str(p) + ".meta") for p in list(paths))
    paths.add(project / "ProjectSettings/GraphicsSettings.asset")
    paths.update(dump / s["name"] / "variants.json" for s in shaders.values())
    return {str(p): hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(paths) if p.exists()}


def guid(path):
    m = re.search(r"^guid: (\w+)", read(str(path) + ".meta"), re.M)
    if not m:
        raise ValueError("Missing GUID: " + str(path))
    return m[1]


@dataclass
class Program:
    sub: int
    index: int
    mode: str
    code: str
    groups: list
    branches: dict
    variants: dict


def pragma_groups(code):
    groups = []
    for kind, words in re.findall(r"^[ \t]*#pragma[ \t]+(multi_compile\w*|shader_feature\w*)\b([^\n]*)", code, re.M):
        if kind not in ("multi_compile", "shader_feature"):
            raise ValueError("Unsupported pragma: " + kind)
        opts = words.split("//")[0].split()
        if not opts:
            raise ValueError("Empty pragma: " + kind)
        if len(opts) == 1 and kind == "shader_feature":
            opts.insert(0, "__")
        groups.append((kind, [None if w.strip("_") == "" else w for w in opts]))
    return groups


def parse_programs(text, variants):
    """Follow ShaderDiff's subshader/pass numbering, including UsePass; fail closed.

    CB comments identify every variant body, including unlabelled trailing #else and
    unconditional single variants. Nested platform #ifs are tracked, not counted.
    Dump order is independently verified against all labelled conditions.
    """
    result = []
    sub = index = -1
    header = []
    lines = iter(text.splitlines())
    for line in lines:
        t = line.strip()
        if t == "SubShader {":
            sub += 1
            index = -1
        elif t == "Pass {" or t.startswith("UsePass "):
            index += 1
            header = []
        elif t == "CGPROGRAM":
            code_lines = list(itertools.takewhile(lambda x: x.strip() != "ENDCG", lines))
            code = "\n".join(code_lines)
            mode = re.search(r'"LightMode"\s*=\s*"([^"]+)"', "\n".join(header), re.I)
            stage_variants = {s: [dict(v, id=i + 1) for i, v in enumerate(variants)
                                  if (v["subShader"], v["pass"], v["stage"]) == (sub, index, s)]
                              for s in ("vs", "ps")}
            branches = {"vs": [], "ps": []}
            stack = []
            for ln in code_lines:
                stripped = ln.strip()
                directive = re.match(r"#(if|ifdef|ifndef|elif|else|endif)\b(.*)", stripped)
                if directive:
                    op, rest = directive.groups()
                    if op in ("if", "ifdef", "ifndef"):
                        # Only variant chains have the program-type comment.
                        is_variant = bool(re.search(r"// .*:DX11(?:Vertex|Pixel)SM40", rest))
                        stack.append([is_variant, rest.split("//")[0].strip() if is_variant else None])
                    elif op in ("elif", "else"):
                        if not stack:
                            raise ValueError("Unbalanced preprocessor")
                        if stack[-1][0]:
                            stack[-1][1] = rest.split("//")[0].strip() if op == "elif" else None
                    else:
                        stack.pop()
                marker = re.search(r"// CBs for DX11(Vertex|Pixel)SM40", ln)
                if not marker:
                    continue
                s = "vs" if marker[1] == "Vertex" else "ps"
                ordinal = len(branches[s])
                v = stage_variants[s][ordinal]
                conditions = [x[1] for x in stack if x[0]]
                condition = conditions[-1] if conditions else None
                if condition is not None:
                    if not re.fullmatch(r"\w+(?:\s*&&\s*\w+)*", condition):
                        raise ValueError("Unsupported variant condition: " + condition)
                    if set(re.findall(r"\w+", condition)) != set(v["keywords"]):
                        raise ValueError("Dump/body order differs at " + str((sub, index, s, ordinal)))
                branches[s].append(dict(id=v["id"], keywords=v["keywords"], condition=condition))
            for s in branches:
                if len(branches[s]) != len(stage_variants[s]) or not branches[s]:
                    raise ValueError("Dump/body count differs: " + str((sub, index, s)))
                if len(branches[s]) > 1 and branches[s][-1]["condition"] is not None:
                    raise ValueError("Variant chain has no fallback")
            result.append(Program(sub, index, mode[1] if mode else "Always", code,
                                  pragma_groups(code), branches, stage_variants))
        else:
            header.append(line)
    return result


def compiled_keywords(groups, enabled):
    selected = set()
    conflicts = []
    for _, opts in groups:
        on = [k for k in opts if k and k in enabled]
        if len(on) > 1:
            conflicts.append(on)
        choice = on[0] if on else opts[0]
        if choice:
            selected.add(choice)
    return selected, conflicts


def branch_for(program, stage, keywords):
    for b in program.branches[stage]:
        if b["condition"] is None or set(b["keywords"]) <= keywords:
            return b
    raise ValueError("No branch")


def original_groups(variants):
    return _original_groups(tuple(tuple(v["keywords"]) for v in variants))


@lru_cache(maxsize=None)
def _original_groups(keyword_lists):
    """Recover independent choices from the original inventory, never from pragmas.

    Two options must never coexist and must replace each other in at least one
    exact original combination. Ambiguous/incomplete inventories remain visible
    as missing/ambiguous original rows rather than nearest-variant guesses.
    """
    combos = {frozenset(keys) for keys in keyword_lists}
    universe = set().union(*combos)
    mandatory = set.intersection(*(set(c) for c in combos))
    groups = []
    for k in sorted(universe - mandatory):
        group = next((g for g in groups if all(not any({k, o} <= c for c in combos) for o in g)
                      and any(any(k in c and (c - {k}) | {o} in combos for c in combos) for o in g)), None)
        if group is None:
            groups.append([k])
        else:
            group.append(k)
    # The first dumped original combination defines a default only when a set is mandatory.
    # This assumption is reported and empirically checked; it is not a proof of the
    # original binary's unpublished nearest-variant selection algorithm.
    order = list(dict.fromkeys(k for keys in keyword_lists for k in keys))
    return mandatory, [(sorted(g, key=order.index), all(set(g) & c for c in combos)) for g in groups]


def expected_keywords(variants, enabled):
    mandatory, groups = original_groups(variants)
    selected = set(mandatory)
    conflicts = []
    for group, required in groups:
        on = set(group) & enabled
        if len(on) > 1:
            conflicts.append(sorted(on))
        selected.update(on if on else group[:1] if required else [])
    return selected, conflicts


def compare(program, stage, requested, actual=None):
    variants = program.variants[stage]
    expected, original_conflicts = expected_keywords(variants, requested)
    compiled, conflicts = compiled_keywords(program.groups, requested)
    if actual is not None:
        compiled = set(actual)
    expected_variants = [v["id"] for v in variants if set(v["keywords"]) == expected]
    selected = branch_for(program, stage, compiled)
    if original_conflicts:
        reason = "ambiguous-original-request"
    elif len(expected_variants) != 1:
        reason = "no-exact-original" if not expected_variants else "duplicate-original-keywords"
    elif conflicts and actual is None:
        reason = "conflicting-pragma-options"
    elif selected["id"] != expected_variants[0]:
        reason = "wrong-fallback" if selected["condition"] is None else "wrong-conditional-branch"
    else:
        reason = "match"
    return dict(stage=stage, requested=sorted(requested), expected_keywords=sorted(expected),
                expected=expected_variants, compiled=sorted(compiled), branch=selected["id"],
                branch_keywords=selected["keywords"], fallback=selected["condition"] is None,
                reason=reason, pragma_conflicts=conflicts)


def engine_request(mode, setup):
    keys = set(SETUPS[setup])
    if mode == "ForwardBase":
        keys -= {"POINT", "SPOT", "POINT_COOKIE", "DIRECTIONAL_COOKIE"}
        keys.add("DIRECTIONAL")
    elif mode != "ForwardAdd":
        keys -= {"POINT", "SPOT", "POINT_COOKIE", "DIRECTIONAL_COOKIE", "DIRECTIONAL"}
    return keys


def applicable(mode):
    if mode == "ForwardAdd":
        return ["dir", "point", "spot", "directional_cookie", "point_cookie", "spot_cookie"]
    if mode == "ForwardBase":
        return list(SETUPS)
    return ["dark"]


def instrument(program, name):
    """Preserve every pragma and condition, replace bodies with branch/keyword IDs.

    Float RenderTextures avoid color-space/8-bit encoding ambiguity. A constant
    vertex varying records the *vertex* compiler's branch and keyword decisions.
    Pixel 0 = (VS id, PS id, marker, 1); remaining pixels = (VS macro, PS macro, marker, 1).
    The extra renderer pass is color-masked so ForwardAdd never sums branch IDs.
    """
    keys = sorted(set(ENGINE) | {k for _, opts in program.groups for k in opts if k})
    if len(keys) > 63:
        raise ValueError("Probe needs >64 pixels")
    code = ["#pragma vertex vert", "#pragma fragment frag", "#pragma target 3.0",
            '#include "UnityCG.cginc"']
    code += re.findall(r"^\s*#pragma (?:multi_compile|shader_feature)\b[^\n]*", program.code, re.M)
    for stage, symbol in (("vs", "ETG_V"), ("ps", "ETG_P")):
        branches = program.branches[stage]
        for i, b in enumerate(branches):
            if b["condition"] is not None:
                code.append(("#if " if i == 0 else "#elif ") + b["condition"])
            elif len(branches) > 1:
                code.append("#else")
            code.append("#define %s %d" % (symbol, b["id"]))
        if len(branches) > 1:
            code.append("#endif")
    # Up to 63 bits, in four exactly represented 16-bit integer words per stage.
    for i, key in enumerate(keys):
        code += ["#if defined(" + key + ")", "#define ETG_K%d %d.0" % (i, 1 << (i % 16)),
                 "#else", "#define ETG_K%d 0.0" % i, "#endif"]
    words = [" + ".join("ETG_K%d" % i for i in range(j * 16, min((j + 1) * 16, len(keys)))) or "0.0"
             for j in range(4)]
    code += ["struct v2f { float4 pos:SV_POSITION; float2 uv:TEXCOORD0; float4 bits:TEXCOORD1; float id:TEXCOORD2; };",
             "v2f vert(appdata_base v) { v2f o; o.pos=UnityObjectToClipPos(v.vertex); o.uv=v.texcoord.xy;",
             "o.bits=float4(%s); o.id=ETG_V; return o; }" % ",".join(words),
             "float4 frag(v2f i):SV_Target { int cell=(int)floor(i.uv.x*64.0);",
             "if(cell==0) return float4(i.id,ETG_P,173.0,1.0);",
             "float4 psbits=float4(%s);" % ",".join(words)]
    for i in range(len(keys)):
        j, bit = divmod(i, 16)
        code.append("if(cell==%d) return float4(fmod(floor(floor(i.bits[%d]+0.25)/%d.0),2.0),"
                    "fmod(floor(psbits[%d]/%d.0),2.0),173.0,1.0);" % (i + 1, j, 1 << bit, j, 1 << bit))
    code += ["return float4(0,0,173,1); }"]
    target = 'Pass { Tags { "LightMode"="%s" } Cull Off ZTest Always ZWrite Off Blend Off\nCGPROGRAM\n%s\nENDCG\n}' % (program.mode, "\n".join(code))
    # ForwardAdd needs a ForwardBase. The diagnostic base cannot obscure the target.
    base = ('Pass { Tags { "LightMode"="ForwardBase" } ColorMask 0 ZWrite Off\n'
            'CGPROGRAM\n#pragma vertex v\n#pragma fragment f\n#include "UnityCG.cginc"\n'
            'float4 v(float4 p:POSITION):SV_POSITION{return UnityObjectToClipPos(p);}\n'
            'float4 f():SV_Target{return 0;}\nENDCG\n}')
    return ('Shader "%s" { SubShader { Tags { "RenderType"="Opaque" }\n%s\n%s\n} Fallback Off }\n'
            % (name, base if program.mode == "ForwardAdd" else "", target)), keys


def legacy_groups(variants):
    """Exact pre-bug-17 column grouping, kept as an in-memory sensitivity mutation."""
    combos = [sorted(v["keywords"]) for v in variants]
    mandatory = set.intersection(*(set(c) for c in combos))
    least = min(map(len, combos))
    shortest = [[k for k in c if k not in mandatory] for c in combos if len(c) == least]
    groups = [("multi_compile", [k]) for k in sorted(mandatory)]
    consumed = set(mandatory)
    for i in range(least - len(mandatory)):
        group = list(dict.fromkeys(c[i] for c in shortest))
        groups.append(("multi_compile", group))
        consumed.update(group)
    for k in sorted(set().union(*map(set, combos)) - consumed):
        groups.append(("shader_feature", [None, k]))
    return groups


def scan(project, dump, filter_re):
    shaders = {}
    unchecked = []
    for path in sorted((project / "Assets").rglob("*.shader")):
        if TEMP in path.as_posix() or not Path(str(path) + ".meta").exists():
            continue
        text = read(path)
        m = re.search(r'\bShader\s+"([^"]+)"', text)
        if not m:
            continue
        name = m[1]
        inventory = dump / name / "variants.json"
        entry = dict(path=path, name=name, programs=[])
        shaders[guid(path)] = entry
        if inventory.exists():
            variants = json.loads(read(inventory))
            try:
                entry["programs"] = parse_programs(text, variants)
            except (ValueError, IndexError) as exc:
                unchecked.append(dict(shader=name, reason="parser: " + str(exc)))
        else:
            unchecked.append(dict(shader=name, reason="no original DXBC inventory"))
    materials = []
    for path in sorted((project / "Assets").rglob("*.mat")):
        if TEMP in path.as_posix():
            continue
        asset = path.relative_to(project).as_posix()
        text = read(path)
        ref = re.search(r"m_Shader: \{fileID: (\d+), guid: (\w+)", text)
        shader = shaders.get(ref[2]) if ref else None
        if filter_re and not re.search(filter_re, asset + " " + (shader["name"] if shader else ""), re.I):
            continue
        # Do not let \s* consume the next YAML field for an empty keyword list.
        kw = re.search(r"^[ \t]*m_ShaderKeywords:[ \t]*([^\r\n]*)", text, re.M)
        materials.append(dict(path=asset, keywords=set(kw[1].strip('"').split()) if kw else set(), shader=shader))
        if not shader:
            unchecked.append(dict(material=asset, reason="builtin/unresolved shader reference " + (ref[0] if ref else "null")))
    return shaders, materials, unchecked


def static_audit(shaders, materials, out):
    counts = Counter()
    issues = []
    rows = 0
    with (out / "static.jsonl").open("w", encoding="utf-8") as stream:
        for mat in materials:
            shader = mat["shader"]
            if not shader or not shader["programs"]:
                continue
            known = {k for p in shader["programs"] for _, opts in p.groups for k in opts if k}
            unknown = sorted(mat["keywords"] - known)
            if unknown:
                issues.append(dict(shader=shader["name"], material=mat["path"], reason="material-keywords-absent-from-pragmas", keywords=unknown))
            for p in shader["programs"]:
                for setup in applicable(p.mode):
                    request = mat["keywords"] | engine_request(p.mode, setup)
                    for stage in ("vs", "ps"):
                        row = dict(shader=shader["name"], material=mat["path"], subshader=p.sub,
                                   pass_index=p.index, mode=p.mode, setup=setup, **compare(p, stage, request))
                        stream.write(json.dumps(row, ensure_ascii=False) + "\n")
                        counts[row["reason"]] += 1
                        rows += 1
                        if row["reason"] != "match":
                            issues.append(row)
    # Exhaust every combination, including shaders without a material. Stage-specific
    # keywords not used by that stage are projected away, as in the original binary.
    exhaustive = Counter()
    with (out / "combinations.jsonl").open("w", encoding="utf-8") as stream:
        for shader in shaders.values():
            for p in shader["programs"]:
                if math.prod(len(opts) for _, opts in p.groups) > 100000:
                    issues.append(dict(shader=shader["name"], reason="combination-limit", subshader=p.sub, pass_index=p.index))
                    continue
                for combo in itertools.product(*(opts for _, opts in p.groups)):
                    keys = set(combo) - {None}
                    for s in ("vs", "ps"):
                        universe = {k for v in p.variants[s] for k in v["keywords"]}
                        wanted = keys & universe
                        exact = [v["id"] for v in p.variants[s] if set(v["keywords"]) == wanted]
                        branch = branch_for(p, s, keys)
                        reason = ("no-exact-original-combination" if not exact else "match" if branch["id"] in exact
                                  else "wrong-fallback" if branch["condition"] is None else "shadowed-original")
                        row = dict(shader=shader["name"], subshader=p.sub, pass_index=p.index, stage=s,
                                   compiled=sorted(keys), expected=exact, branch=branch["id"], reason=reason)
                        exhaustive[reason] += 1
                        stream.write(json.dumps(row) + "\n")
                        if reason != "match":
                            issues.append(row)
    return counts, exhaustive, issues, rows


def stripping(shaders, materials, project):
    settings = read(project / "ProjectSettings/GraphicsSettings.asset")
    section = settings.split("m_AlwaysIncludedShaders:")[1].split("m_PreloadedShaders:")[0]
    included = set(re.findall(r"guid: (\w+)", section))
    result = []
    for shader_guid, shader in shaders.items():
        mats = [m for m in materials if m["shader"] is shader]
        for p in shader["programs"]:
            features = [g for g in p.groups if g[0] == "shader_feature"]
            if not features:
                continue
            used = {tuple(sorted(compiled_keywords(features, m["keywords"])[0])) for m in mats}
            for combo in itertools.product(*(opts for _, opts in features)):
                keys = sorted(set(combo) - {None})
                retained = shader_guid in included or tuple(keys) in used
                result.append(dict(shader=shader["name"], shader_asset=shader["path"].relative_to(project).as_posix(),
                                   subshader=p.sub, pass_index=p.index,
                                   feature_keywords=keys, status="retained-candidate" if retained else "strip-risk",
                                   always_included=shader_guid in included))
    return result


def sensitivity(shaders):
    names = ["LitCutoutUber", "LitBlendUber", "LitCutoutUber_ColorEmissive", "BurnableSprite", "DisplacerBeast"]
    result = []
    for name in names:
        entry = next((s for s in shaders.values() if s["name"] == "Brave/" + name), None)
        if not entry:
            raise ValueError("Sensitivity shader missing: " + name)
        for p in entry["programs"]:
            if p.mode != "ForwardAdd":
                continue
            original = p.groups
            for setup in ("point", "spot"):
                request = expected_keywords(p.variants["vs"], engine_request(p.mode, setup))[0]
                current = [compare(p, s, request) for s in ("vs", "ps")]
                try:
                    p.groups = legacy_groups(p.variants["vs"] + p.variants["ps"])
                    legacy = [compare(p, s, request) for s in ("vs", "ps")]
                finally:
                    p.groups = original
                ok = all(r["reason"] == "match" for r in current) and all(r["reason"] != "match" for r in legacy)
                result.append(dict(shader=entry["name"], setup=setup, current=current, legacy=legacy, passed=ok))
    return result


def prepare(project, shaders, materials, out, legacy=False, filter_re=None):
    temp = project / TEMP
    if temp.exists():
        raise ValueError("Temporary folder already exists (not deleted automatically): " + str(temp))
    temp.mkdir()
    jobs = []
    try:
        for mode in ("Always", "ForwardBase", "ForwardAdd"):
            groups = []
            if mode == "ForwardBase":
                groups.append(("multi_compile", ["DIRECTIONAL"]))
            elif mode == "ForwardAdd":
                groups.append(("multi_compile", ["POINT", "DIRECTIONAL", "SPOT", "POINT_COOKIE", "DIRECTIONAL_COOKIE"]))
            groups += [("multi_compile", [None, k]) for k in ("LIGHTPROBE_SH", "VERTEXLIGHT_ON", "SHADOWS_SCREEN")]
            branch = dict(id=1, keywords=[], condition=None)
            ref = Program(0, 0, mode, "\n".join("#pragma " + kind + " " + " ".join(k or "__" for k in opts) for kind, opts in groups),
                          groups, {"vs": [branch], "ps": [branch]}, {})
            source, _ = instrument(ref, "Hidden/EtgVariant/Engine" + mode)
            ref_path = temp / ("Engine" + mode + ".shader")
            ref_path.write_text(source, encoding="utf-8")
            Path(str(ref_path) + ".meta").write_text("fileFormatVersion: 2\nguid: " + hashlib.sha256(mode.encode()).hexdigest()[:32] + "\nShaderImporter:\n  externalObjects: {}\n", encoding="utf-8")
        for shader in shaders.values():
            mats = [m for m in materials if m["shader"] is shader]
            if not mats:
                if filter_re and not re.search(filter_re, shader["name"], re.I):
                    continue
                mats = [dict(path="<shader-only>/" + shader["name"], keywords=set(), shader=shader)]
            for p in shader["programs"]:
                # Other LightModes need shadow/baking/deferred/legacy render paths;
                # never pretend a camera's forward color pass exercised them.
                if p.mode not in ("Always", "ForwardBase", "ForwardAdd") or p.sub != 0:
                    continue
                pass_mats = list(mats)
                if shader["name"].split("/")[-1] in SENSITIVITY_SHADERS and p.mode == "ForwardAdd":
                    material_states = sorted({tuple(k for k in v["keywords"] if k not in ENGINE) for v in p.variants["vs"]})
                    pass_mats += [dict(path="<sensitivity>/" + shader["name"] + "/" + str(i), keywords=set(keys), shader=shader)
                                  for i, keys in enumerate(material_states)]
                shader_asset = shader["path"].relative_to(project).as_posix()
                token = hashlib.sha256((shader_asset + str((p.sub, p.index))).encode()).hexdigest()[:16]
                shader_path = temp / (token + ".shader")
                original = p.groups
                if legacy and shader["name"].split("/")[-1] in SENSITIVITY_SHADERS and p.mode == "ForwardAdd":
                    p.groups = legacy_groups(p.variants["vs"] + p.variants["ps"])
                    saved_code = p.code
                    p.code = "\n".join("#pragma " + kind + " " + " ".join(k or "__" for k in opts) for kind, opts in p.groups)
                else:
                    saved_code = p.code
                try:
                    source, keys = instrument(p, "Hidden/EtgVariant/" + token)
                    effective_groups = p.groups
                finally:
                    p.groups, p.code = original, saved_code
                shader_path.write_text(source, encoding="utf-8")
                shader_guid = hashlib.sha256(("shader" + token).encode()).hexdigest()[:32]
                Path(str(shader_path) + ".meta").write_text("fileFormatVersion: 2\nguid: " + shader_guid + "\nShaderImporter:\n  externalObjects: {}\n", encoding="utf-8")
                for mat in pass_mats:
                    mat_token = hashlib.sha256((mat["path"] + token).encode()).hexdigest()[:16]
                    mat_path = temp / (mat_token + ".mat")
                    synthetic = mat["path"].startswith("<")
                    mat_keywords = (expected_keywords(p.variants["vs"], set())[0] - ENGINE
                                    if mat["path"].startswith("<shader-only>/") else mat["keywords"])
                    source_mat = ("%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n--- !u!21 &2100000\nMaterial:\n"
                                  "  serializedVersion: 6\n  m_Name: VariantProbe\n  m_Shader: {fileID: 4800000, guid: " + shader_guid + ", type: 3}\n"
                                  "  m_ShaderKeywords: " + " ".join(sorted(mat_keywords)) + "\n  m_CustomRenderQueue: -1\n"
                                  "  m_SavedProperties:\n    serializedVersion: 3\n    m_TexEnvs: []\n    m_Floats: []\n    m_Colors: []\n"
                                  if synthetic else read(project / mat["path"]))
                    clone = re.sub(r"(m_Shader: \{fileID: )\d+(, guid: )\w+", r"\g<1>4800000\g<2>" + shader_guid, source_mat)
                    mat_path.write_text(clone, encoding="utf-8")
                    Path(str(mat_path) + ".meta").write_text("fileFormatVersion: 2\nguid: " + hashlib.sha256(mat_token.encode()).hexdigest()[:32] + "\nNativeFormatImporter:\n  mainObjectFileID: 2100000\n", encoding="utf-8")
                    for setup in applicable(p.mode):
                        jobs.append(dict(id=len(jobs), shader=shader["name"], material=mat["path"],
                                         shaderAsset=shader_asset,
                                         clone=mat_path.relative_to(project).as_posix(), subshader=p.sub,
                                         pass_index=p.index, mode=p.mode, setup=setup, keys=keys,
                                         pragmaGroups=effective_groups, legacy=legacy and effective_groups != original,
                                         materialKeywords=sorted(mat_keywords)))
        write_json(out / "jobs.json", {"jobs": jobs})
        return temp
    except BaseException:
        shutil.rmtree(temp)
        raise


def empirical_report(out, shaders, strip):
    jobs = json.loads(read(out / "jobs.json"))["jobs"]
    by_name = {s["name"]: s for s in shaders.values()}
    by_asset = {s["path"].as_posix(): s for s in shaders.values()}
    retention = {(r["shader_asset"], r["subshader"], r["pass_index"], tuple(r["feature_keywords"])): r["status"] for r in strip}
    measurements = {}
    path = out / "observed.jsonl"
    if path.exists():
        for line in read(path).splitlines():
            r = json.loads(line)
            measurements[r["id"]] = r
    rows = []
    for job in jobs:
        shader = (next(s for asset, s in by_asset.items() if asset.endswith("/" + job["shaderAsset"]))
                  if "shaderAsset" in job else by_name[job["shader"]])
        p = next(p for p in shader["programs"] if (p.sub, p.index) == (job["subshader"], job["pass_index"]))
        observed = measurements.get(job["id"], {"status": "missing-measurement"})
        for stage in ("vs", "ps"):
            base = {k: job[k] for k in ("id", "shader", "material", "subshader", "pass_index", "mode", "setup")}
            base["shader_asset"] = next(asset[asset.index("/Assets/") + 1:] for asset, s in by_asset.items() if s is shader)
            if observed["status"] != "ok":
                rows.append(dict(base, stage=stage, reason="unverified-" + observed["status"]))
                continue
            actual = observed[stage + "Keywords"]
            # The setup fixes the light type. For optional engine toggles, use the
            # independent reference probe (not macros surviving the audited pragmas).
            request = set(job["materialKeywords"]) | set(observed["engineKeywords"])
            r = compare(p, stage, request, actual)
            r["observed_branch"] = observed[stage]
            r["model_branch"] = r["branch"]
            effective_groups = job.get("pragmaGroups", p.groups)
            r["model_compiled"], model_conflicts = compiled_keywords(effective_groups, request)
            r["model_compiled"] = sorted(r["model_compiled"])
            relevant = {k for _, opts in effective_groups for k in opts if k}
            r["pragma_model_matches"] = None if model_conflicts else set(actual) & relevant == set(r["model_compiled"])
            r["legacy_mutation"] = job.get("legacy", False)
            features = {k for kind, opts in p.groups if kind == "shader_feature" for k in opts if k}
            r["build_retention"] = (retention.get((base["shader_asset"], p.sub, p.index, tuple(sorted(set(actual) & features))), "unknown")
                                    if features else "multi_compile")
            if observed[stage] != r["branch"]:
                r["reason"] = "instrumentation-model-disagrees"
            rows.append(dict(base, **r))
    with (out / "empirical.jsonl").open("w", encoding="utf-8") as stream:
        for row in rows:
            stream.write(json.dumps(row, ensure_ascii=False) + "\n")
    return rows


def engine_coverage(out):
    """Report what the reference actually enabled, including failed shadow attempts."""
    jobs = json.loads(read(out / "jobs.json"))["jobs"]
    path = out / "observed.jsonl"
    observations = {r["id"]: r for r in (json.loads(l) for l in read(path).splitlines())} if path.exists() else {}
    seen = defaultdict(set)
    for j in jobs:
        seen[(j["mode"], j["setup"])]
        r = observations.get(j["id"])
        if r and r["status"] == "ok":
            seen[(j["mode"], j["setup"])].add(tuple(r["engineKeywords"]))
    rows = []
    for (mode, setup), states in sorted(seen.items()):
        wanted = engine_request(mode, setup)
        actual = set().union(*(set(s) for s in states))
        rows.append(dict(mode=mode, setup=setup, observed=[list(s) for s in sorted(states)],
                         missing_requested=sorted(wanted - actual)))
    write_json(out / "engine-coverage.json", rows)
    return rows


def report(out, counts, exhaustive, issues, unchecked, strip, observed, sens, materials, static_only):
    groups = defaultdict(list)
    for row in issues + [r for r in observed if r["reason"] != "match"]:
        groups[(row.get("shader", "?"), row["reason"])].append(row)
    write_json(out / "issues.json", [dict(shader=s, reason=r, count=len(rows), examples=rows[:8])
                                     for (s, r), rows in sorted(groups.items())])
    write_json(out / "unchecked.json", unchecked)
    write_json(out / "stripping.json", strip)
    write_json(out / "sensitivity.json", sens)
    summary = dict(materials=len(materials), static=dict(counts), combinations=dict(exhaustive),
                   empirical=dict(Counter(r["reason"] for r in observed)),
                   pragma_model=dict(Counter(str(r.get("pragma_model_matches")) for r in observed)),
                   selected_build_retention=dict(Counter(r.get("build_retention", "unverified") for r in observed)),
                   stripping=dict(Counter(r["status"] for r in strip)), unchecked=len(unchecked),
                   sensitivity_passed=len(sens) == 10 and all(r["passed"] for r in sens),
                   legacy_mutation=any(r.get("legacy_mutation") for r in observed))
    write_json(out / "summary.json", summary)
    lines = ["# Shader variant selection", "", "```json", json.dumps(summary, indent=2), "```", "",
             "Rows: `static.jsonl` (every material/pass/stage/setup), `combinations.jsonl` (all pragma combinations),",
             "`empirical.jsonl` (actual Unity branch IDs and VS/PS macros), `stripping.json`, `unchecked.json`.", "",
             "## Oracle and limits", "",
             "- Exact original keyword inventories are the oracle. No nearest-variant guess is called a match.",
             "- Static defaults: mandatory original groups use their first dumped option; pragma groups use their first option.",
             "  Reconstruction of original mutually exclusive sets is an assumption, checked by the render for covered requests.",
             "- Original stages omit irrelevant keywords (e.g. VERTEXLIGHT_ON in PS); comparison projects to each stage's inventory.",
             "- Static setups are candidate engine states, not a claim that every material uses them during gameplay.",
             "- Empirical probes retain pragmas/conditions and record both stages separately in linear ARGBFloat.",
             "  Bodies/render state are replaced for visibility: this checks selection, not original drawing or shader correctness.",
             "- Only first-subshader Always/ForwardBase/ForwardAdd camera color passes are rendered. ShadowCaster, Meta,",
             "  deferred/legacy passes, UsePass, fallbacks, lower subshaders, baked/dynamic lightmaps, fog, instancing, VR,",
             "  soft/point/spot shadow permutations and real gameplay/global keyword changes are not empirically covered.",
             "- Hard directional shadows, SH probe usage and ForceVertex lights are attempted. The independent engine probe",
             "  records which macros actually became active; absence is listed below, never counted as coverage.",
             "- Strip-risk is a conservative material/Always Included inventory, not an actual player build audit. Bundle",
             "  dependency inclusion, ShaderVariantCollections, engine/platform stripping and runtime EnableKeyword need a build trace.",
             "  Always Included retains feature candidates; editor-only materials can overestimate player retention.",
             "- Bug 17 sensitivity uses the exact old column algorithm in memory; `--legacy` also renders that mutation",
             "  on diagnostic copies of the five affected shaders. No source patch or project original is modified.", ""]
    if static_only:
        lines += ["**Empirical pass not run (`--static-only`). All Unity selection results remain predictions.**", ""]
    else:
        if summary["legacy_mutation"]:
            lines += ["**Intentional bug-17 mutation on diagnostic copies. This is a sensitivity run, not the installed shaders.**", ""]
        coverage = engine_coverage(out)
        lines += ["## Observed engine states", "", "| Mode / setup | Actual engine keywords | Requested but inactive |",
                  "|---|---|---|"]
        for c in coverage:
            lines.append("| %s / %s | %s | %s |" % (c["mode"], c["setup"], "; ".join(" ".join(s) or "(none)" for s in c["observed"]), " ".join(c["missing_requested"]) or "—"))
        lines += ["", "Inactive requested keywords above are **not empirically verified**. See `engine-coverage.json`.", ""]
    lines += ["## Findings by shader and reason", "", "| Shader | Reason | Rows |", "|---|---|---:|"]
    lines += ["| %s | %s | %d |" % (s, r, len(rows)) for (s, r), rows in sorted(groups.items())]
    lines += ["", "Examples are in `issues.json`; full material paths and expected/selected IDs are in the JSONL files.",
              "", "## Unchecked assets", ""]
    lines += ["- " + json.dumps(r, ensure_ascii=False) for r in unchecked]
    (out / "report.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    print(json.dumps(summary, indent=2))
    print("Report: " + str(out / "report.md"))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--project", type=Path, default=ROOT / "ripped/ExportedProject")
    parser.add_argument("--dump", type=Path, default=ROOT / "tools/uscs/dump")
    parser.add_argument("--out", type=Path, default=ROOT / "port/build/variant-selection")
    parser.add_argument("--unity", type=Path, default=Path("C:/Program Files/Unity/Hub/Editor/2017.4.27f1/Editor/Unity.exe"))
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--static-only", action="store_true")
    mode.add_argument("--report-only", action="store_true")
    parser.add_argument("--filter", help="regex on shader name or material path (static and render rows)")
    parser.add_argument("--legacy", action="store_true", help="render the pre-bug-17 pragma mutation on temporary shaders")
    args = parser.parse_args()
    project, out = args.project.resolve(), args.out.resolve()
    if not (project / "Assets").is_dir() or not list(args.dump.rglob("variants.json")):
        parser.error("Project Assets or original variants missing; see --project/--dump and decompile_shaders.sh")
    out.mkdir(parents=True, exist_ok=True)
    shaders, materials, unchecked = scan(project, args.dump.resolve(), args.filter)
    inputs = snapshot(shaders, materials, project, args.dump.resolve())
    if args.report_only:
        saved = json.loads(read(out / "inputs.json"))
        if saved != dict(filter=args.filter, hashes=inputs):
            parser.error("Assets/inventory/filter changed since rendering; rerun the empirical pass")
    for shader in shaders.values():
        for p in shader["programs"]:
            if p.mode not in ("Always", "ForwardBase", "ForwardAdd") or p.sub != 0:
                unchecked.append(dict(shader=shader["name"], subshader=p.sub, pass_index=p.index,
                                      reason="empirical: unsupported camera path " + p.mode))
    counts, exhaustive, issues, _ = static_audit(shaders, materials, out)
    strip = stripping(shaders, materials, project)
    # Write failed sensitivity details too, rather than leaving a stale passing report.
    sens = sensitivity(shaders)
    observed = []
    failed = False
    if not args.static_only:
        if not args.report_only:
            running = subprocess.run(["tasklist", "/FI", "IMAGENAME eq Unity.exe"], capture_output=True, text=True)
            if "Unity.exe" in running.stdout or (project / "Temp/UnityLockfile").exists():
                parser.error("Close Unity before batchmode. The project is locked.")
            if not args.unity.exists():
                parser.error("Unity executable missing: " + str(args.unity))
            harness = project / "Assets/Editor/EtgWebBuild/EtgVariantSelection.cs"
            harness.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(ROOT / "port/UnityAssets/Editor/EtgWebBuild/EtgVariantSelection.cs", harness)
            settings = {p: p.read_bytes() for p in (project / "ProjectSettings").iterdir() if p.is_file()}
            temp = prepare(project, shaders, materials, out, args.legacy, args.filter)
            write_json(out / "inputs.json", dict(filter=args.filter, hashes=inputs))
            (out / "observed.jsonl").unlink(missing_ok=True)
            try:
                run = subprocess.run([str(args.unity), "-batchmode", "-projectPath", str(project), "-force-d3d11",
                                      "-executeMethod", "EtgVariantSelection.Run", "-variantOut", str(out),
                                      "-logFile", str(out / "editor.log")], timeout=3600)
                if run.returncode:
                    raise RuntimeError("Unity exited %d; see %s" % (run.returncode, out / "editor.log"))
            except (RuntimeError, subprocess.TimeoutExpired) as exc:
                unchecked.append(dict(reason="Unity run failed: " + str(exc)))
                failed = True
            finally:
                # Only remove the exact folder created by this invocation, inside the project.
                if temp.resolve() != project / TEMP:
                    raise RuntimeError("Unexpected cleanup path")
                shutil.rmtree(temp)
                Path(str(temp) + ".meta").unlink(missing_ok=True)
                for path, contents in settings.items():
                    if not path.exists() or path.read_bytes() != contents:
                        path.write_bytes(contents)
            if snapshot(shaders, materials, project, args.dump.resolve()) != inputs:
                raise RuntimeError("Original shader/material/inventory changed during the run; do not trust measurements")
        observed = empirical_report(out, shaders, strip)
        # A successful process without a measurement per job is an incomplete run.
        if any(r["reason"] == "unverified-missing-measurement" for r in observed):
            unchecked.append(dict(reason="Unity run omitted jobs; see empirical.jsonl"))
            failed = True
    report(out, counts, exhaustive, issues, unchecked, strip, observed, sens, materials, args.static_only)
    if failed or len(sens) != 10 or not all(r["passed"] for r in sens):
        raise SystemExit(2)


if __name__ == "__main__":
    main()
