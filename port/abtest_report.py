"""Compares the renders of two port/abtest.sh runs (D3D11 vs GLES) and writes report.html + report.md.

Usage: python abtest_report.py <abtest dir> [--a d3d11] [--b gles30] [--tol 4] [--min-frac 0.002]
  <abtest dir>/<run>/manifest.jsonl + PNGs come from EtgAbTest.cs; a run named "<a>.2" / "<b>.2" (same API again)
  measures the noise floor: renders that differ between two runs on one API are flagged as nondeterministic.
A pixel differs when any RGBA channel differs by more than --tol (0..255); an entry differs when more than
--min-frac of its pixels do. Smaller differences are listed separately ("minor"), and so are differences made of
pixels in small blobs ("speckle"): typically the frac(sin(dot(p, (12.9898, 78.233))) * 43758.5) hash, whose sin() of large
arguments the D3D and GL compilers evaluate differently - precision, not a different computation.
"""
import argparse
import html
import json
import sys
from pathlib import Path

import numpy as np
from PIL import Image

# AbTestProbe.shader / AbTestLightProbe.shader cells (4x4, row 0 at the bottom of the image) and whether
# D3D11/GLES are expected to differ there
LIGHT_PROBE_CELLS = [
    ("_LightColor0", False), ("_WorldSpaceLightPos0", False), ("ShadeSH9", False), ("VERTEXLIGHT_ON/LIGHTPROBE_SH/DIRECTIONAL", False),
    ("unity_4LightPosX0", False), ("unity_4LightAtten0", False), ("unity_LightColor[0]", False), ("unity_LightColor[1]", False),
    ("POINT _LightColor0", False), ("POINT attenuation", False), ("POINT_COOKIE _LightColor0", False), ("POINT_COOKIE attenuation", False),
    ("SPOT _LightColor0", False), ("SPOT attenuation", False), ("DIRECTIONAL(_COOKIE) add _LightColor0", False), ("DIRECTIONAL(_COOKIE) add attenuation", False),
]
PROBE_CELLS = [
    ("_Time", False), ("_SinTime", False), ("unity_DeltaTime", False), ("_ProjectionParams", True),
    ("_ScreenParams", False), ("_ZBufferParams", True), ("unity_OrthoParams", False), ("clip-space xy", True),
    ("SV_Position / _ScreenParams", True), ("defines UV_STARTS_AT_TOP/REVERSED_Z/GLES3", True),
    ("_WorldSpaceCameraPos", False), ("ambient", False),
    ("UNITY_MATRIX_VP row 1", True), ("UNITY_NEAR_CLIP_VALUE", True), ("uv", False), ("white", False),
]


def load_run(root: Path, name: str):
    run = root / name
    manifest = run / "manifest.jsonl"
    if not manifest.exists():
        return None
    entries = {}
    for line in manifest.read_text(encoding="utf-8").splitlines():
        if not line.strip():
            continue
        e = json.loads(line)
        key = (e["kind"], e["id"], e.get("setup") or "")
        entries[key] = e
    env = {}
    if (run / "environment.json").exists():
        env = json.loads((run / "environment.json").read_text(encoding="utf-8"))
    return {"name": name, "dir": run, "entries": entries, "env": env}


_cache = {}


def image(run, entry):
    if not entry or not entry.get("file"):
        return None
    path = run["dir"] / entry["file"]
    if path not in _cache:
        try:
            _cache[path] = np.asarray(Image.open(path).convert("RGBA"), dtype=np.int16)
        except (OSError, ValueError) as e:  # missing, or damaged (once: a single flipped bit in a written PNG)
            entry["status"] = "unreadable png" if path.exists() else "missing png"
            print(f"warning: {path}: {e}")
            _cache[path] = None
    return _cache[path]


def compare(a, b, tol):
    if a is None or b is None or a.shape != b.shape:
        return None
    d = np.abs(a - b).max(axis=2)
    over = d > tol
    # no 2x2 block of differing pixels: dots and 1-pixel lines (a texel boundary hit by one row or column)
    thin = not bool((over[:-1, :-1] & over[1:, :-1] & over[:-1, 1:] & over[1:, 1:]).any())
    return {"max": int(d.max()), "frac": float(over.mean()), "mean": float(d.mean()), "isolated": small_blob_share(over),
            "thin": thin, "diff": d}


def small_blob_share(mask, small=16):
    """Share of the set pixels that lie in 8-connected blobs of at most `small` pixels."""
    h, w = mask.shape
    todo = set(zip(*np.nonzero(mask)))
    if not todo:
        return 0.0
    total, in_small = len(todo), 0
    while todo:
        stack = [todo.pop()]
        size = 0
        while stack:
            y, x = stack.pop()
            size += 1
            for dy in (-1, 0, 1):
                for dx in (-1, 0, 1):
                    q = (y + dy, x + dx)
                    if q in todo:
                        todo.remove(q)
                        stack.append(q)
        if size <= small:
            in_small += size
    return in_small / total


def shader_sources(directory: Path):
    """shader name -> source text (for annotations)"""
    out = {}
    for f in directory.rglob("*.shader") if directory.is_dir() else []:
        text = f.read_text(encoding="utf-8-sig", errors="replace")
        if text.startswith('Shader "'):
            out[text[8:text.index('"', 8)]] = text
    return out


def same_as(img, ref, tol):
    return img is not None and ref is not None and img.shape == ref.shape and int(np.abs(img - ref).max()) <= tol


def write_diff(d, a, path: Path):
    # differing pixels in red (brightness ~ size of the difference) over a dimmed grey copy of A
    grey = a[:, :, :3].mean(axis=2) * 0.3
    out = np.stack([np.clip(grey + d * 8, 0, 255), grey, grey], axis=2).astype(np.uint8)
    path.parent.mkdir(parents=True, exist_ok=True)
    Image.fromarray(out).save(path)


def probe_cells(d, cells):
    h, w = d.shape
    rows = []
    for i, (name, expected) in enumerate(cells):
        cx, cy = i % 4, i // 4
        # PNG rows go top-down, the probe's cell row 0 is at the bottom (uv.y = 0)
        y0, y1 = h - (cy + 1) * h // 4, h - cy * h // 4
        cell = d[y0 + 2:y1 - 2, cx * w // 4 + 2:(cx + 1) * w // 4 - 2]
        rows.append((name, expected, int(cell.max())))
    return rows


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("root")
    ap.add_argument("--a", default="d3d11")
    ap.add_argument("--b", default="gles30")
    ap.add_argument("--tol", type=int, default=4)
    ap.add_argument("--min-frac", type=float, default=0.002)
    ap.add_argument("--shaders", help="shader sources (default: <etg>/tools/uscs/out, then the Unity 6.6 project's Assets: "
                                      "mods' shaders are compiled from source)")
    args = ap.parse_args()
    root = Path(args.root)
    if args.shaders:
        sources = shader_sources(Path(args.shaders))
    else:
        etg = root.resolve().parents[2]
        sources = shader_sources(etg / "ripped" / "Unity6Project" / "Assets")
        sources.update(shader_sources(etg / "tools" / "uscs" / "out"))  # the decompiled text wins
    A, B = load_run(root, args.a), load_run(root, args.b)
    if A is None or B is None:
        sys.exit(f"need {root / args.a}/manifest.jsonl and {root / args.b}/manifest.jsonl")
    repeats = {r["name"]: r for r in (load_run(root, args.a + ".2"), load_run(root, args.b + ".2")) if r}

    # noise floor: the same API twice
    noise = {}  # key -> worst repeat comparison
    determinism = []
    for rep in repeats.values():
        base = A if rep["name"].startswith(args.a + ".") else B
        differing, worst, total = [], 0, 0
        for key, e in base["entries"].items():
            r = rep["entries"].get(key)
            c = compare(image(base, e), image(rep, r), 0)
            if c is None:
                continue
            total += 1
            if c["max"] > 0:
                differing.append((key, c["max"], c["frac"]))
                worst = max(worst, c["max"])
                prev = noise.get(key)
                if prev is None or c["max"] > prev["max"]:
                    noise[key] = {"max": c["max"], "frac": c["frac"], "run": rep["name"]}
        determinism.append({"run": f"{base['name']} vs {rep['name']}", "total": total, "differing": differing, "worst": worst})

    backgrounds = {}
    for run in (A, B):
        for key, e in run["entries"].items():
            if e["kind"] == "background":
                backgrounds[(run["name"], key[2])] = image(run, e)

    results = []
    keys = sorted(set(A["entries"]) | set(B["entries"]))
    for key in keys:
        kind, ident, setup = key
        if kind == "background":
            continue
        ea, eb = A["entries"].get(key), B["entries"].get(key)
        meta = ea or eb
        ia, ib = image(A, ea), image(B, eb)
        r = {"key": key, "kind": kind, "id": ident, "setup": setup, "meta": meta, "a": ea, "b": eb,
             "status_a": ea["status"] if ea else "missing", "status_b": eb["status"] if eb else "missing"}
        c = compare(ia, ib, args.tol)
        r["cmp"] = c
        r["noise"] = noise.get(key)
        r["empty_a"] = same_as(ia, backgrounds.get((A["name"], setup)), args.tol)
        r["empty_b"] = same_as(ib, backgrounds.get((B["name"], setup)), args.tol)
        if ident in ("_probe", "_lightprobe"):
            r["cls"] = "probe"
        elif c is None or r["status_a"] != "ok" or r["status_b"] != "ok":
            r["cls"] = "failed"
        elif r["empty_a"] and r["empty_b"]:
            r["cls"] = "empty"
        elif c["frac"] > args.min_frac and (c["isolated"] >= 0.7 or c["thin"]) and c["frac"] < 0.1:
            r["cls"] = "speckle"
        elif c["frac"] > args.min_frac:
            r["cls"] = "differs"
        elif c["max"] > args.tol:
            r["cls"] = "minor"
        else:
            r["cls"] = "same"
        if "43758" in sources.get((meta or {}).get("shader") or "", ""):
            r["hash"] = True
        if c is not None and r["cls"] in ("differs", "speckle", "minor", "probe"):
            name = f"{ident}__{setup}.png" if setup else f"{ident}.png"
            r["diff_file"] = f"report/diff/{kind}/{name}"
            write_diff(c["diff"], ia, root / r["diff_file"])
        results.append(r)

    def score(r):
        c = r["cmp"]
        return (-c["frac"], -c["max"]) if c else (0, 0)

    by_cls = {}
    for r in results:
        by_cls.setdefault(r["cls"], []).append(r)
    for v in by_cls.values():
        v.sort(key=score)

    write_html(root, A, B, args, by_cls, determinism, results)
    write_md(root, A, B, args, by_cls, determinism)
    counts = {k: len(v) for k, v in by_cls.items()}
    print("abtest report:", ", ".join(f"{k} {counts.get(k, 0)}" for k in CLASSES),
          "->", root / "report.html")


CLASSES = ("differs", "speckle", "minor", "failed", "empty", "same")


def shader_of(r):
    return (r["meta"] or {}).get("shader") or ""


def describe(r):
    m = r["meta"] or {}
    parts = [m.get("shader") or "", m.get("keywords") or ""]
    if m.get("path"):
        parts.append(m["path"])
    return " · ".join(p for p in parts if p)


def metrics(r):
    c = r["cmp"]
    s = f"{c['frac'] * 100:.2f}% px, max {c['max']}, mean {c['mean']:.2f}, {c['isolated'] * 100:.0f}% in small blobs{', 1 px thin' if c['thin'] else ''}" if c else "—"
    if r.get("hash"):
        s += " · shader uses the sin()*43758 hash"
    if r.get("noise"):
        n = r["noise"]
        s += f" · noise ({n['run']}): max {n['max']}, {n['frac'] * 100:.2f}% px"
    return s


CSS = """
:root { --bg: #fafafa; --fg: #1d1d1f; --muted: #6b6b70; --line: #ddd; --chk1: #ccc; --chk2: #eee; --bad: #c0392b; --warn: #b9770e; }
@media (prefers-color-scheme: dark) { :root { --bg: #18181b; --fg: #e8e8ea; --muted: #9a9aa2; --line: #333;
  --chk1: #444; --chk2: #2a2a2e; --bad: #ff6b5b; --warn: #f0b429; } }
body { background: var(--bg); color: var(--fg); font: 14px/1.45 system-ui, sans-serif; margin: 0 16px 48px; }
h1 { font-size: 22px; } h2 { margin-top: 32px; border-bottom: 1px solid var(--line); padding-bottom: 4px; }
.muted { color: var(--muted); } code { font-size: 12px; }
table { border-collapse: collapse; } td, th { border-bottom: 1px solid var(--line); padding: 3px 8px; text-align: left; vertical-align: top; }
.item { border-bottom: 1px solid var(--line); padding: 10px 0; }
.item .title { font-weight: 600; word-break: break-all; } .item .desc { color: var(--muted); font-size: 12px; word-break: break-all; }
.imgs { display: flex; gap: 8px; flex-wrap: wrap; margin-top: 6px; }
.imgs figure { margin: 0; } .imgs figcaption { font-size: 11px; color: var(--muted); }
.imgs img { display: block; max-width: min(46vw, 480px); image-rendering: pixelated;
  background: repeating-conic-gradient(var(--chk1) 0 25%, var(--chk2) 0 50%) 0 0 / 16px 16px; }
.gallery .imgs img { width: 192px; }
.bad { color: var(--bad); } .warn { color: var(--warn); }
details summary { cursor: pointer; }
"""


def img_block(A, B, r):
    out = ['<div class="imgs">']
    for run, e, label in ((A, r["a"], A["name"]), (B, r["b"], B["name"])):
        if e and e.get("file"):
            out.append(f'<figure><img loading="lazy" src="{run["name"]}/{e["file"]}"><figcaption>{label}</figcaption></figure>')
    if r.get("diff_file"):
        out.append(f'<figure><img loading="lazy" src="{r["diff_file"]}"><figcaption>difference (red)</figcaption></figure>')
    out.append("</div>")
    return "".join(out)


def write_html(root, A, B, args, by_cls, determinism, results):
    h = []
    e = html.escape
    h.append(f"<!doctype html><html><head><meta charset='utf-8'><meta name='viewport' content='width=device-width'>"
             f"<title>D3D11 vs GLES</title><style>{CSS}</style></head><body>")
    h.append("<h1>D3D11 vs GLES A/B test</h1>")
    h.append("<table>")
    for run in (A, B):
        env = run["env"]
        h.append(f"<tr><th>{e(run['name'])}</th><td>{e(env.get('device', '?'))} · {e(env.get('deviceVersion', ''))} · "
                 f"shader level {e(str(env.get('shaderLevel', '')))} · {e(str(env.get('tier', '')))} · "
                 f"{e(env.get('started', ''))}</td></tr>")
    h.append("</table>")
    h.append(f"<p class='muted'>A pixel differs when a channel differs by more than {args.tol}/255; an entry "
             f"<b>differs</b> above {args.min_frac * 100:.2f}% of its pixels, below that it is <b>minor</b>. Sorted by "
             f"the share of differing pixels. “noise” = the same API rendered twice.</p>")
    counts = {k: len(v) for k, v in by_cls.items()}
    h.append("<table><tr>" + "".join(f"<th>{k}</th>" for k in CLASSES) + "</tr><tr>" +
             "".join(f"<td>{counts.get(k, 0)}</td>" for k in CLASSES) + "</tr></table>")

    h.append("<h2>Determinism</h2>")
    if not determinism:
        h.append("<p class='warn'>No repeat run (&lt;api&gt;.2): noise floor unknown.</p>")
    for d in determinism:
        h.append(f"<p><b>{e(d['run'])}</b>: {len(d['differing'])} of {d['total']} renders differ, worst {d['worst']}/255.</p>")
        if d["differing"]:
            h.append("<ul>" + "".join(f"<li>{e(k[1])} {e(k[2])}: max {m}, {f * 100:.2f}% px</li>" for k, m, f in d["differing"][:50]) + "</ul>")

    for r in sorted(by_cls.get("probe", []), key=lambda r: r["key"]):
        what = "built-in uniforms" if r["id"] == "_probe" else "forward lighting data"
        cells = PROBE_CELLS if r["id"] == "_probe" else LIGHT_PROBE_CELLS
        h.append(f"<h2>Probe [{e(r['setup'])}]: {what} per API</h2><table><tr><th>cell</th><th>max diff</th><th></th></tr>")
        for name, expected, m in probe_cells(r["cmp"]["diff"], cells) if r["cmp"] else []:
            note = ("expected (API)" if expected else "<span class='bad'>unexpected</span>") if m > args.tol else ""
            h.append(f"<tr><td>{e(name)}</td><td>{m}</td><td>{note}</td></tr>")
        h.append("</table>" + img_block(A, B, r))

    # per-shader summary of what differs
    shaders = {}
    for r in by_cls.get("differs", []) + by_cls.get("speckle", []) + by_cls.get("minor", []):
        if r["kind"] == "gallery":
            shaders.setdefault(shader_of(r), []).append(r)
    if shaders:
        h.append("<h2>Differing shaders</h2><table><tr><th>shader</th><th>renders</th><th>worst</th><th>setups</th></tr>")
        for s, rs in sorted(shaders.items(), key=lambda kv: -max(x["cmp"]["frac"] for x in kv[1])):
            worst = max(rs, key=lambda x: x["cmp"]["frac"])
            h.append(f"<tr><td>{e(s)}</td><td>{len(rs)}</td><td>{worst['cmp']['frac'] * 100:.2f}% · max {worst['cmp']['max']}</td>"
                     f"<td>{e(' '.join(sorted(set(x['setup'] for x in rs))))}</td></tr>")
        h.append("</table>")

    titles = {"differs": "Differs", "speckle": "Speckle: small scattered blobs or 1-pixel lines (precision: the sin hash, texel boundaries)",
              "minor": "Minor differences", "failed": "Not rendered on one or both APIs"}
    for cls in ("differs", "speckle", "minor", "failed"):
        rs = by_cls.get(cls, [])
        h.append(f"<h2>{titles[cls]} ({len(rs)})</h2>")
        for r in rs:
            klass = "item gallery" if r["kind"] == "gallery" else "item"
            status = "" if cls != "failed" else f" <span class='bad'>{e(A['name'])}: {e(r['status_a'])}, {e(B['name'])}: {e(r['status_b'])}</span>"
            empty = ""
            if r.get("empty_a") != r.get("empty_b"):
                empty = f" <span class='bad'>empty on {e(A['name'] if r['empty_a'] else B['name'])} only</span>"
            noisy = " <span class='warn'>nondeterministic</span>" if r.get("noise") else ""
            m = r["meta"] or {}
            extra = m.get("error") or m.get("filled") or ""
            h.append(f"<div class='{klass}'><div class='title'>{e(r['kind'])}: {e(r['id'])} [{e(r['setup'])}]{status}{empty}{noisy}</div>"
                     f"<div class='desc'>{e(describe(r))}</div><div class='desc'>{e(metrics(r))}</div>"
                     f"<div class='desc'>{e(extra)}</div>{img_block(A, B, r)}</div>")

    for cls, title in (("empty", "Empty on both APIs — not verified"), ("same", "Same")):
        rs = by_cls.get(cls, [])
        h.append(f"<h2>{title} ({len(rs)})</h2><details><summary>list</summary><ul>")
        for r in sorted(rs, key=lambda r: r["key"]):
            h.append(f"<li>{e(r['kind'])}: {e(r['id'])} [{e(r['setup'])}] <span class='muted'>{e(shader_of(r))}</span></li>")
        h.append("</ul></details>")
    h.append("</body></html>")
    (root / "report.html").write_text("\n".join(h), encoding="utf-8")


def write_md(root, A, B, args, by_cls, determinism):
    md = [f"# D3D11 vs GLES A/B test: {A['name']} vs {B['name']}", ""]
    counts = {k: len(v) for k, v in by_cls.items()}
    md.append("| " + " | ".join(CLASSES) + " |")
    md.append("|---" * len(CLASSES) + "|")
    md.append("| " + " | ".join(str(counts.get(k, 0)) for k in CLASSES) + " |")
    md.append("")
    md.append(f"Pixel tolerance {args.tol}/255, entry threshold {args.min_frac * 100:.2f}% of pixels. Images: report.html.")
    md.append("")
    md.append("## Determinism")
    if not determinism:
        md.append("No repeat run: noise floor unknown.")
    for d in determinism:
        md.append(f"- {d['run']}: {len(d['differing'])} of {d['total']} renders differ, worst {d['worst']}/255")
    for cls, title in (("differs", "Differs"), ("speckle", "Speckle (small blobs / 1-px lines)"), ("minor", "Minor"), ("failed", "Not rendered")):
        rs = by_cls.get(cls, [])
        md.append("")
        md.append(f"## {title} ({len(rs)})")
        for r in rs:
            st = f" — {A['name']}: {r['status_a']}, {B['name']}: {r['status_b']}" if cls == "failed" else ""
            md.append(f"- `{r['kind']}: {r['id']}` [{r['setup']}] {shader_of(r)}{st} — {metrics(r)}")
    rs = by_cls.get("empty", [])
    md.append("")
    md.append(f"## Empty on both APIs, not verified ({len(rs)})")
    md.append(", ".join(sorted(set(shader_of(r) or r["id"] for r in rs))))
    (root / "report.md").write_text("\n".join(md) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
