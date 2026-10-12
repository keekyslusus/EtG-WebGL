"""Measures the impulse response of a bus's effect chain in EtG's real Wwise, for the browser engine's
ConvolverNode.

    python port/audio/measure_ir.py            # all busses in REVERB_BUSSES
    options: --cases N (sounds used, default 240), --length S (IR seconds, default 4)

The Dungeon bus, which every sound effect goes through, runs RoomVerb + Matrix Reverb; aux busses
(Foyer: the Breach's reverb, Dimension...) get sends from most sounds and are switched by states. Rather than
re-implementing those plugins from their parameters, the harness (port/WwiseRef) renders a set of
sound effect cases twice: with the bus's effects and with them removed (SetBusEffect), the master
bus's effects removed in both so only the bus's chain differs. Same seeds -> same random choices, so
per channel  wet = dry * h.  h is estimated over all cases at once (Wiener: sum Wet Dry* / sum |Dry|^2),
then checked on cases that were left out of the estimate. For an aux bus, the signal entering it is
the difference between renders with the bus on and off (its -96 dB state).

Output: port/build/audio/ir/<bus id>.wav (32-bit float stereo, 48 kHz); build_audio.py ships it.
"""
import argparse
import json
import shutil
import subprocess
import sys
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
PORT_DIR = HERE.parent
ROOT = PORT_DIR.parent
sys.path.insert(0, str(HERE))
import audiodiff_cases  # noqa: E402
import audiodiff_features  # noqa: E402
import build_audio  # noqa: E402

RATE = 48000
MASTER = 3803692087
DUNGEON = 608898761
REVERB_BUSSES = {DUNGEON: "Dungeon"}   # regular busses: bus id -> name (all their effects measured)
OUT = PORT_DIR / "build" / "audio" / "ir"
WORK = PORT_DIR / "build" / "audio" / "ir_work"


def bus_chain(nodes, node_id):
    """Busses a node's output goes through, nearest first."""
    bus = 0
    cur = nodes.get(str(node_id))
    while cur and not bus:
        bus = cur.get("bus", 0)
        cur = nodes.get(str(cur.get("parent", 0)))
    out = []
    while bus and str(bus) in nodes:
        out.append(bus)
        bus = nodes[str(bus)].get("parent", 0)
    return out


def first_sound(nodes, nid, depth=0):
    n = nodes.get(str(nid))
    if not n or depth > 8:
        return None
    if n["type"] == "sound":
        return nid
    kids = [c for ks in n.get("switches", {}).values() for c in ks] if n["type"] == "switch" else n.get("children", [])
    for c in kids:
        s = first_sound(nodes, c, depth + 1)
        if s:
            return s
    return None


def aux_sends(nodes, node_id):
    """User aux busses a sound sends to: the nearest node in its hierarchy with sends."""
    cur = nodes.get(str(node_id))
    while cur:
        if cur.get("aux"):
            return cur["aux"]
        cur = nodes.get(str(cur.get("parent", 0)))
    return []


def select_cases(doc, bus, count, tail, aux=False):
    nodes = doc["nodes"]
    cases, reset = audiodiff_cases.make_cases(doc, repeats=1)
    chosen = []
    for c in cases:
        if c["kind"] != "event":
            continue
        play = next(s for s in c["steps"] if s["op"] == "postEvent")
        target = next(a["target"] for a in doc["events"][str(play["args"][0])] if a["type"] == 4)
        snd = first_sound(nodes, target)
        if snd and bus in (aux_sends(nodes, snd) if aux else bus_chain(nodes, snd)):
            chosen.append(c)
    step = max(1, len(chosen) // count)
    chosen = chosen[::step][:count]
    for c in chosen:
        c["duration"] = round(c["duration"] + tail, 3)  # room for the reverb tail
    return chosen, reset


def run(cases_file, out_dir, remove):
    shutil.rmtree(out_dir, ignore_errors=True)
    args = [str(PORT_DIR / "build" / "WwiseRef" / "WwiseRef.exe"), str(ROOT), str(cases_file), str(out_dir)]
    if remove:
        args += ["--remove-fx", ",".join(f"{b}:{i}" for b, i in remove)]
    subprocess.run(args, check=True, stdout=subprocess.DEVNULL)


def estimate(pairs, length):
    n_fft = 1 << int(np.ceil(np.log2(max(len(w) for w, _ in pairs) + length)))
    num = np.zeros((2, n_fft // 2 + 1), complex)
    den = np.zeros((2, n_fft // 2 + 1))
    for wet, dry in pairs:
        for ch in range(2):
            W = np.fft.rfft(wet[:, ch], n_fft)
            D = np.fft.rfft(dry[:, ch], n_fft)
            num[ch] += W * np.conj(D)
            den[ch] += np.abs(D) ** 2
    eps = 1e-4 * den.mean(axis=1, keepdims=True)
    h = np.fft.irfft(num / (den + eps), n_fft)[:, :length]
    fade = int(0.05 * RATE)
    h[:, -fade:] *= np.linspace(1, 0, fade)
    return h.T.astype(np.float32)


def trim(h, window=0.1):
    """Cuts the response where its 100 ms envelope reaches the estimate's noise floor (3 dB above the
    level of the last second) or 60 dB below the peak, with a 50 ms fade: every second of response
    is convolution work in the browser."""
    n = int(window * RATE)
    e = (h[:len(h) // n * n].astype(np.float64) ** 2).sum(axis=1).reshape(-1, n).mean(axis=1)
    env = 10 * np.log10(np.maximum(e, 1e-30))
    floor = np.median(env[-int(1 / window):]) + 3
    stop = max(floor, env.max() - 60)
    top = int(np.argmax(env))  # search after the peak: an aux reverb's pre-delay starts quiet
    below = np.nonzero(env[top + 1:] <= stop)[0]
    end = (below[0] + top + 1) * n if len(below) else len(h)
    h = h[:end].copy()
    fade = min(int(0.05 * RATE), end // 4)
    h[-fade:] *= np.linspace(1, 0, fade)[:, None]
    print(f"   response cut at {end / RATE:.2f} s")
    return h


def predict_error(pairs, h):
    """Residual energy of dry * h against wet, relative to wet (dB)."""
    err, ref = 0.0, 0.0
    for wet, dry in pairs:
        n_fft = 1 << int(np.ceil(np.log2(len(wet) + len(h))))
        for ch in range(2):
            pred = np.fft.irfft(np.fft.rfft(dry[:, ch], n_fft) * np.fft.rfft(h[:, ch], n_fft), n_fft)[:len(wet)]
            err += np.sum((wet[:, ch] - pred) ** 2)
            ref += np.sum(wet[:, ch] ** 2)
    return 10 * np.log10(err / ref)


def write_float_wav(path, x):
    x = np.ascontiguousarray(x, dtype="<f4")
    data = x.tobytes()
    header = b"RIFF" + (36 + len(data)).to_bytes(4, "little") + b"WAVE" + b"fmt " + (16).to_bytes(4, "little")
    header += (3).to_bytes(2, "little") + (2).to_bytes(2, "little") + RATE.to_bytes(4, "little")
    header += (RATE * 8).to_bytes(4, "little") + (8).to_bytes(2, "little") + (32).to_bytes(2, "little")
    header += b"data" + len(data).to_bytes(4, "little")
    path.write_bytes(header + data)


def mute_state(node):
    """(state group, state) that turns an aux bus off (its bus volume -96 dB), if any."""
    for group, states in node.get("states", []):
        for state, inst in states:
            yield group, state, inst


def load_pairs(cases, wet_dir, dry_dir, off_dir=None):
    pairs = []
    for c in cases:
        wet = audiodiff_features.read_wav((wet_dir / f"{c['id']}.wav").read_bytes()).astype(np.float64)
        dry = audiodiff_features.read_wav((dry_dir / f"{c['id']}.wav").read_bytes()).astype(np.float64)
        n = min(len(wet), len(dry))
        wet, dry = wet[:n], dry[:n]
        if off_dir:
            off = audiodiff_features.read_wav((off_dir / f"{c['id']}.wav").read_bytes()).astype(np.float64)[:n]
            wet, dry = wet - off, dry - off
        if np.abs(dry).max() > 1e-3:
            pairs.append((wet, dry))
    return pairs


def solve(pairs, length, out_path):
    train, test = pairs[::2] + pairs[3::4], pairs[1::4]
    h = estimate(train, length)
    if test:
        print(f"   {len(pairs)} usable cases; dry*h vs. wet on {len(test)} held-out cases: "
              f"residual {predict_error(test, h):.1f} dB (plain dry: {predict_error(test, np.eye(1, length).T.repeat(2, 1)):.1f} dB)")
    h = trim(estimate(pairs, length))
    peak = np.argmax(np.abs(h).max(axis=1))
    print(f"   direct path at {peak / RATE * 1000:.1f} ms ({20 * np.log10(np.abs(h[peak]).max()):.1f} dB), tail energy (after 20 ms) "
          f"{10 * np.log10(np.sum(h[int(0.02 * RATE):] ** 2) / np.sum(h ** 2)):.1f} dB of total")
    write_float_wav(out_path, h)
    print("  ", out_path)


def measure_aux(doc, bus, node, args, length):
    """An aux bus's effect chain. The signal reaching the bus is isolated as the difference between the
    bus on (state None) and off (its -96 dB state); Dungeon's and the master's effects are off throughout."""
    nodes = doc["nodes"]
    off = None
    for group, state, inst in mute_state(node):
        if nodes.get(str(inst), {}).get("props", {}).get("busvol", 0) <= -96:
            off = (group, state)
    if off is None:
        print("   no state turns it off, skipped")
        return
    cases, reset = select_cases(doc, bus, args.cases, args.length, aux=True)
    if not cases:
        print("   nothing sends to it, skipped")
        return
    base = [(MASTER, 0), (MASTER, 1), (DUNGEON, 0), (DUNGEON, 1)]
    fx = [(bus, i) for i, _ in node.get("fx", [])]
    on_file = WORK / f"{bus}.json"
    on_file.write_text(json.dumps({"reset": reset, "cases": cases}), encoding="utf-8")
    off_cases = [dict(c, steps=[{"t": 0, "op": "setState", "args": list(off)}] + c["steps"]) for c in cases]
    off_file = WORK / f"{bus}_off.json"
    off_file.write_text(json.dumps({"reset": reset, "cases": off_cases}), encoding="utf-8")
    run(off_file, WORK / "off", base)
    run(on_file, WORK / "dry", base + fx)
    run(on_file, WORK / "wet", base)
    pairs = load_pairs(cases, WORK / "wet", WORK / "dry", WORK / "off")
    if len(pairs) < 4:
        print(f"   the aux path adds nothing audible ({len(pairs)} cases), skipped")
        return
    solve(pairs, length, OUT / f"{bus}.wav")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--cases", type=int, default=240)
    ap.add_argument("--length", type=float, default=4.0)
    ap.add_argument("--only", type=int, nargs="*", help="bus ids to measure (default: all)")
    args = ap.parse_args()
    doc = json.loads((Path(build_audio.OUT) / "audio.json").read_text(encoding="utf-8"))
    OUT.mkdir(parents=True, exist_ok=True)
    WORK.mkdir(parents=True, exist_ok=True)
    build_audio_ref()
    length = int(args.length * RATE)
    for bus, name in REVERB_BUSSES.items():
        if args.only is not None and bus not in args.only:
            continue
        print(f"== {name} ({bus})")
        cases, reset = select_cases(doc, bus, args.cases, args.length)
        cases_file = WORK / f"{bus}.json"
        cases_file.write_text(json.dumps({"reset": reset, "cases": cases}), encoding="utf-8")
        master = [(MASTER, 0), (MASTER, 1)]
        fx = [(bus, i) for i, _ in doc["nodes"][str(bus)].get("fx", [])]
        run(cases_file, WORK / "wet", master)
        run(cases_file, WORK / "dry", master + fx)
        solve(load_pairs(cases, WORK / "wet", WORK / "dry"), length, OUT / f"{bus}.wav")
    for bus_s, node in doc["nodes"].items():
        if node["type"] == "auxbus" and node.get("fx") and (args.only is None or int(bus_s) in args.only):
            print(f"== aux bus {doc['names'].get(bus_s, bus_s)} ({bus_s})")
            measure_aux(doc, int(bus_s), node, args, length)
    shutil.rmtree(WORK, ignore_errors=True)


def build_audio_ref():
    subprocess.run(["dotnet", "build", str(PORT_DIR / "WwiseRef"), "-c", "Release", "-o", str(PORT_DIR / "build" / "WwiseRef"),
                    "-nologo", "-v", "q"], check=True, stdout=subprocess.DEVNULL)


if __name__ == "__main__":
    main()
