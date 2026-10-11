"""Compares the features of both renderers per case (repeats of a random case are pooled) and writes
report.html + report.md into the audiodiff output directory.

Flags (a case can have several; the first one listed is its category):
  missing   no render from one side (an error in the browser engine, see audiodiff.py's output)
  silent    one side silent, the other not
  level     RMS differs by more than LEVEL_DB (or by more than the spread of the repeats)
  timing    onset differs by more than ONSET_S, or the audible end by more than END_S / END_FRAC
  spectrum  energy-weighted spectral centroid differs by more than SPECTRUM_OCT octaves
  pan       stereo balance differs by more than PAN_DB
  shape     (music) the envelopes differ by more than SHAPE_DB in some second
"""
import html
import json
import math
from pathlib import Path

import numpy as np

import audiodiff_features as F

LEVEL_DB = 1.5
ONSET_S = 0.06
END_S, END_FRAC = 0.15, 0.10
SPECTRUM_OCT = 0.35
PAN_DB = 3.0
SHAPE_DB = 6.0
FLAGS = ("missing", "silent", "level", "timing", "spectrum", "pan", "shape")


def spread(vals):
    return (max(vals) - min(vals)) if len(vals) > 1 else 0.0


def med(vals):
    return float(np.median(vals)) if vals else 0.0


def envelope(feats):
    """Median (over repeats) mono envelope in dB."""
    n = min(len(f["env_l"]) for f in feats)
    envs = [10 * np.log10((10 ** (f["env_l"][:n].astype(np.float64) / 10) + 10 ** (f["env_r"][:n].astype(np.float64) / 10)) / 2)
            for f in feats]
    return np.median(np.array(envs), axis=0)


def compare(out: Path):
    doc = json.loads((out / "cases.json").read_text(encoding="utf-8"))
    groups = {}
    for c in doc["cases"]:
        groups.setdefault(c["name"], {"kind": c["kind"], "ids": [], "duration": c["duration"]})["ids"].append(c["id"])
    results = []
    for name, g in groups.items():
        sides = {}
        for side in ("ref", "ours"):
            sides[side] = [F.load(p) for cid in g["ids"] if (p := out / "features" / side / f"{cid}.npz").exists()]
        r = {"name": name, "kind": g["kind"], "ids": g["ids"], "flags": [], "metrics": {}}
        ref, ours = sides["ref"], sides["ours"]
        if not ref or not ours:
            r["flags"].append("missing")
            r["score"] = 1000
            results.append(r)
            continue
        ref_silent = sum(f["silent"] for f in ref) > len(ref) / 2
        ours_silent = sum(f["silent"] for f in ours) > len(ours) / 2
        m = r["metrics"]
        for k in ("rms", "peak", "onset", "end", "centroid", "balance"):
            m[k] = (med([f[k] for f in ref]), med([f[k] for f in ours]))
        r["silent"] = (ref_silent, ours_silent)
        score = 0.0
        if ref_silent != ours_silent:
            r["flags"].append("silent")
            score += 500
        elif not ref_silent:
            d_level = m["rms"][1] - m["rms"][0]
            tol = max(LEVEL_DB, (spread([f["rms"] for f in ref]) + spread([f["rms"] for f in ours])) / 2)
            if abs(d_level) > tol:
                r["flags"].append("level")
                score += abs(d_level) - tol
            d_on = m["onset"][1] - m["onset"][0]
            d_end = m["end"][1] - m["end"][0]
            end_tol = max(END_S, END_FRAC * m["end"][0])
            if abs(d_on) > ONSET_S or abs(d_end) > end_tol:
                r["flags"].append("timing")
                score += 20 * max(abs(d_on) - ONSET_S, 0) + 5 * max(abs(d_end) - end_tol, 0)
            if m["centroid"][0] > 0 and m["centroid"][1] > 0:
                d_oct = math.log2(m["centroid"][1] / m["centroid"][0])
                if abs(d_oct) > SPECTRUM_OCT:
                    r["flags"].append("spectrum")
                    score += 4 * (abs(d_oct) - SPECTRUM_OCT)
            d_pan = m["balance"][1] - m["balance"][0]
            if abs(d_pan) > PAN_DB:
                r["flags"].append("pan")
                score += abs(d_pan) - PAN_DB
            er, eo = envelope(ref), envelope(ours)
            n = min(len(er), len(eo))
            r["env"] = (er[:n], eo[:n])
            if g["kind"] in ("music", "trans", "state") and n >= 50:
                sec = n // 50
                a = er[:sec * 50].reshape(sec, 50).mean(axis=1)
                b = eo[:sec * 50].reshape(sec, 50).mean(axis=1)
                audible = (a > -70) | (b > -70)
                worst = float(np.max(np.abs(a - b)[audible])) if audible.any() else 0.0
                m["shape"] = worst
                if worst > SHAPE_DB:
                    r["flags"].append("shape")
                    score += worst - SHAPE_DB
        r["score"] = score
        if "env" not in r and not (ref_silent and ours_silent):
            er, eo = envelope(ref), envelope(ours)
            n = min(len(er), len(eo))
            r["env"] = (er[:n], eo[:n])
        results.append(r)
    results.sort(key=lambda r: -r["score"])
    return results


def worst_ids(results, n):
    """One case id (the first repeat) per flagged group, worst first, to re-render with audio."""
    return [r["ids"][0] for r in results if r["flags"] and r["flags"] != ["missing"]][:n]


def systematic(results):
    """Level offset statistics over all cases audible on both sides, per kind."""
    out = {}
    for r in results:
        if "rms" in r["metrics"] and not any(r["silent"]):
            out.setdefault(r["kind"], []).append(r["metrics"]["rms"][1] - r["metrics"]["rms"][0])
    return {k: (len(v), float(np.median(v)), float(np.percentile(v, 10)), float(np.percentile(v, 90))) for k, v in out.items()}


def svg_env(env, w=360, h=70):
    if env is None:
        return ""
    er, eo = env
    n = len(er)
    if n < 2:
        return ""
    step = max(1, n // 240)
    def pts(e):
        e = e[::step]
        return " ".join(f"{i * step * w / (n - 1):.1f},{h - (max(v, -90) + 90) * h / 90:.1f}" for i, v in enumerate(e))
    return (f"<svg viewBox='0 0 {w} {h}' width='{w}' height='{h}' class='env'>"
            f"<polyline class='r' points='{pts(er)}'/><polyline class='o' points='{pts(eo)}'/></svg>")


def fmt(r, k, unit="", digits=1):
    if k not in r["metrics"]:
        return ""
    a, b = r["metrics"][k]
    return f"{a:.{digits}f} → {b:.{digits}f}{unit}"


def write(out: Path, results, audio_dir: Path):
    counts = {f: 0 for f in FLAGS}
    by_kind = {}
    for r in results:
        k = by_kind.setdefault(r["kind"], {"cases": 0, **{f: 0 for f in FLAGS}})
        k["cases"] += 1
        for f in r["flags"]:
            counts[f] += 1
            k[f] += 1
    flagged = [r for r in results if r["flags"]]
    sysl = systematic(results)
    e = html.escape

    h = ["<!doctype html><html><head><meta charset='utf-8'><meta name='viewport' content='width=device-width'>",
         "<title>EtG Audiodiff</title><style>",
         ":root{--bg:#fff;--fg:#1d1d1f;--muted:#6e6e73;--line:#d2d2d7;--ref:#2a6fdb;--ours:#e0782a}",
         "@media (prefers-color-scheme:dark){:root{--bg:#161618;--fg:#e8e8ed;--muted:#98989d;--line:#38383a}}",
         "body{background:var(--bg);color:var(--fg);font:14px/1.45 system-ui,sans-serif;margin:0;padding:16px;max-width:1400px}",
         "table{border-collapse:collapse;width:100%;margin:8px 0 24px}td,th{border-bottom:1px solid var(--line);padding:4px 6px;text-align:left;vertical-align:top}",
         "th{color:var(--muted);font-weight:600}.num{text-align:right;font-variant-numeric:tabular-nums}",
         ".flag{display:inline-block;padding:0 6px;border-radius:8px;background:var(--line);margin-right:4px;font-size:12px}",
         "svg.env polyline{fill:none;stroke-width:1.2}svg.env .r{stroke:var(--ref)}svg.env .o{stroke:var(--ours)}",
         ".legend .r{color:var(--ref)}.legend .o{color:var(--ours)}audio{height:28px;width:220px}.wrap{overflow-x:auto}",
         "</style></head><body>",
         "<h1>EtG Audiodiff: Wwise vs. browser engine</h1>",
         f"<p>{len(results)} cases ({sum(len(r['ids']) for r in results)} renders per side), {len(flagged)} flagged. "
         "<span class='legend'>Envelopes: <b class='r'>Wwise</b> / <b class='o'>browser</b>, 0 to −90 dB.</span> "
         "Values are medians over a case's repeats, Wwise → browser.</p>",
         "<h2>Summary</h2><div class='wrap'><table><tr><th>kind</th><th class='num'>cases</th>"
         + "".join(f"<th class='num'>{f}</th>" for f in FLAGS) + "<th class='num'>level Δ median (p10…p90)</th></tr>"]
    for kind, k in sorted(by_kind.items()):
        s = sysl.get(kind)
        lv = f"{s[1]:+.1f} dB ({s[2]:+.1f}…{s[3]:+.1f})" if s else ""
        h.append(f"<tr><td>{kind}</td><td class='num'>{k['cases']}</td>" + "".join(f"<td class='num'>{k[f] or ''}</td>" for f in FLAGS)
                 + f"<td class='num'>{lv}</td></tr>")
    h.append("</table></div>")

    for flag in FLAGS:
        rows = [r for r in flagged if r["flags"][0] == flag]
        if not rows:
            continue
        h.append(f"<h2>{flag} ({len(rows)})</h2><div class='wrap'><table><tr><th>case</th><th>flags</th><th>RMS dB</th><th>onset s</th>"
                 "<th>end s</th><th>centroid Hz</th><th>balance dB</th><th>envelope</th><th>listen</th></tr>")
        for r in rows[:150]:
            cid = r["ids"][0]
            listen = ""
            if (audio_dir / "ref" / f"{cid}.wav").exists() and (audio_dir / "ours" / f"{cid}.wav").exists():
                rel = audio_dir.relative_to(out).as_posix()
                listen = (f"<audio controls preload='none' src='{rel}/ref/{cid}.wav'></audio><br>"
                          f"<audio controls preload='none' src='{rel}/ours/{cid}.wav'></audio>")
            h.append(f"<tr><td>{e(r['name'])}<br><small>{r['kind']} · {cid}</small></td>"
                     f"<td>{''.join(f'<span class=flag>{f}</span>' for f in r['flags'])}</td>"
                     f"<td>{fmt(r, 'rms')}</td><td>{fmt(r, 'onset', '', 2)}</td><td>{fmt(r, 'end', '', 2)}</td>"
                     f"<td>{fmt(r, 'centroid', '', 0)}</td><td>{fmt(r, 'balance')}</td><td>{svg_env(r.get('env'))}</td><td>{listen}</td></tr>")
        if len(rows) > 150:
            h.append(f"<tr><td colspan='9'>… {len(rows) - 150} more in report.md</td></tr>")
        h.append("</table></div>")
    h.append("</body></html>")
    (out / "report.html").write_text("\n".join(h), encoding="utf-8")

    md = ["# EtG audiodiff: Wwise vs. browser engine", "",
          f"{len(results)} cases, {len(flagged)} flagged. Values: median over repeats, Wwise -> browser.", "",
          "| kind | cases | " + " | ".join(FLAGS) + " | level Δ median (p10..p90) |",
          "|---|---|" + "---|" * len(FLAGS) + "---|"]
    for kind, k in sorted(by_kind.items()):
        s = sysl.get(kind)
        md.append(f"| {kind} | {k['cases']} | " + " | ".join(str(k[f] or "") for f in FLAGS)
                  + (f" | {s[1]:+.1f} dB ({s[2]:+.1f}..{s[3]:+.1f}) |" if s else " | |"))
    for flag in FLAGS:
        rows = [r for r in flagged if r["flags"][0] == flag]
        if rows:
            md += ["", f"## {flag} ({len(rows)})", ""]
            for r in rows:
                md.append(f"- {r['name']} [{', '.join(r['flags'])}] ({r['ids'][0]}) RMS {fmt(r, 'rms')}, onset {fmt(r, 'onset', 's', 2)}, "
                          f"end {fmt(r, 'end', 's', 2)}, centroid {fmt(r, 'centroid', 'Hz', 0)}, balance {fmt(r, 'balance', 'dB')}")
    (out / "report.md").write_text("\n".join(md) + "\n", encoding="utf-8")
    print(f"   {len(flagged)}/{len(results)} cases flagged:", ", ".join(f"{f} {n}" for f, n in counts.items() if n))
    print("  ", out / "report.html")


def main(out: Path):
    write(out, compare(out), out / "report_audio")
