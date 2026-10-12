"""A page to listen to Wwise and the browser engine side by side.

    python port/audio/listen.py                  # every event whose sounds go through an effect on a sound
    python port/audio/listen.py --filter REGEX   # audiodiff cases whose name matches instead
    options: --repeats N (random cases: renders with other seeds), --jobs N (browser tabs)

Each case is rendered like audiodiff does it (the game's mix: busses, their reverbs, volume 100): by the
game's Wwise (port/WwiseRef), by the engine (etg-audio.js, working copy) and by the engine without its models
of the effects on sounds. Output: port/build/listen/index.html with the WAVs next to it; open it in a browser.
"""
import argparse
import html
import json
import re
import shutil
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import audiodiff  # noqa: E402
import audiodiff_cases  # noqa: E402
import audiodiff_features  # noqa: E402
import measure_fx  # noqa: E402

OUT = HERE.parent / "build" / "listen"
SIDES = (("ref", "Wwise"), ("ours", "browser"), ("nofx", "browser, effects on sounds off"))


def effects_on_the_way(doc, case, fx_nodes):
    """Plugins of the node effects the case's sounds pass through (switch/state values of the case)."""
    nodes = doc["nodes"]
    values = {s["args"][0]: s["args"][1] for s in case["steps"] if s["op"] in ("setSwitch", "setState")}
    found = set()
    for s in case["steps"]:
        if s["op"] != "postEvent":
            continue
        for a in doc["events"][str(s["args"][0])]:
            if a["type"] == 4:
                sounds = set()
                measure_fx.reached_sounds(nodes, a["target"], values, sounds, set())
                for snd in sounds:
                    for n in measure_fx.actor_chain(nodes, snd):
                        found.update(fx_nodes.get(n, ()))
    return sorted(found)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--filter")
    ap.add_argument("--repeats", type=int, default=1)
    ap.add_argument("--jobs", type=int, default=4)
    args = ap.parse_args()

    doc = measure_fx.load_doc()
    fx_nodes = {}
    for nid, _, fid, plugin in measure_fx.node_effects(doc):
        fx_nodes.setdefault(nid, set()).add(plugin.replace("_", " "))
    cases, reset = audiodiff_cases.make_cases(doc, args.repeats)
    chosen = []
    for c in cases:
        if args.filter:
            if re.search(args.filter, c["name"]):
                chosen.append(dict(c, fx=effects_on_the_way(doc, c, fx_nodes)))
        elif c["kind"] == "event":
            fx = effects_on_the_way(doc, c, fx_nodes)
            if fx:
                chosen.append(dict(c, fx=fx))
    shutil.rmtree(OUT, ignore_errors=True)
    OUT.mkdir(parents=True)
    (OUT / "cases.json").write_text(json.dumps({"reset": reset, "cases": chosen}), encoding="utf-8")
    print(f"   {len(chosen)} cases")
    ids = [c["id"] for c in chosen]
    audiodiff.build_ref()
    audiodiff.render_ref(OUT, ids, OUT / "ref", keep_audio=True)
    audiodiff.render_ours(OUT, ids, args.jobs, wav_dir=OUT / "ours")
    audiodiff.render_ours(OUT, ids, args.jobs, wav_dir=OUT / "nofx", query="&nodefx=off")

    rows = []
    for c in sorted(chosen, key=lambda c: (c["fx"], c["name"])):
        cells, levels = [], []
        for side, _ in SIDES:
            wav = OUT / side / f"{c['id']}.wav"
            if wav.exists():
                f = audiodiff_features.features(audiodiff_features.read_wav(wav.read_bytes()))[0]
                levels.append(f["rms"])
                cells.append(f"<td><audio controls preload='none' src='{side}/{c['id']}.wav'></audio>"
                             f"<small>{f['rms']:.1f} dB</small></td>")
            else:
                cells.append("<td>—</td>")
        rows.append(f"<tr><td>{html.escape(c['name'])}<br><small>{', '.join(c['fx'])}"
                    f"{' · seed ' + str(c['seed']) if args.repeats > 1 else ''}</small></td>{''.join(cells)}</tr>")
    head = "".join(f"<th>{label}</th>" for _, label in SIDES)
    page = f"""<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width">
<title>EtG Sound Comparison</title><style>
:root{{--bg:#fff;--fg:#1d1d1f;--muted:#6e6e73;--line:#d2d2d7}}
@media (prefers-color-scheme:dark){{:root{{--bg:#161618;--fg:#e8e8ed;--muted:#98989d;--line:#38383a}}}}
body{{background:var(--bg);color:var(--fg);font:14px/1.45 system-ui,sans-serif;margin:0;padding:16px;max-width:1200px}}
table{{border-collapse:collapse;width:100%}}td,th{{border-bottom:1px solid var(--line);padding:6px;text-align:left;vertical-align:middle}}
th{{color:var(--muted);font-weight:600}}small{{color:var(--muted);display:block}}audio{{height:30px;width:240px}}
.wrap{{overflow-x:auto}}</style></head><body>
<h1>Wwise vs. browser engine</h1>
<p>The game's mix (busses with their reverbs, volume 100), rendered offline by the game's own Wwise and by
etg-audio.js; the last column without the engine's models of the effects on sounds. Level: RMS over the render.</p>
<div class="wrap"><table><tr><th>event · effects on the way</th>{head}</tr>
{chr(10).join(rows)}
</table></div></body></html>"""
    (OUT / "index.html").write_text(page, encoding="utf-8")
    print("  ", OUT / "index.html")


if __name__ == "__main__":
    main()
