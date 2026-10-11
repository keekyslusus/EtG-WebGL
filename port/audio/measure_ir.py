"""Measures the impulse response of a bus's effect chain in EtG's real Wwise, for the browser engine's
ConvolverNode.

    python port/audio/measure_ir.py            # all busses in REVERB_BUSSES
    options: --cases N (sounds used, default 240), --length S (IR seconds, default 4)

The Dungeon bus, which every sound effect goes through, runs RoomVerb + Matrix Reverb. Rather than
re-implementing those plugins from their parameters, the harness (port/WwiseRef) renders a set of
sound effect cases twice: with the bus's effects and with them removed (SetBusEffect), the master
bus's effects removed in both so only the bus's chain differs. Same seeds -> same random choices, so
per channel  wet = dry * h.  h is estimated over all cases at once (Wiener: sum Wet Dry* / sum |Dry|^2),
then checked on cases that were left out of the estimate.

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
REVERB_BUSSES = {608898761: "Dungeon"}   # bus id -> name (effect indices 0 and 1)
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


def select_cases(doc, bus, count, tail):
    nodes = doc["nodes"]
    cases, reset = audiodiff_cases.make_cases(doc, repeats=1)
    chosen = []
    for c in cases:
        if c["kind"] != "event":
            continue
        play = next(s for s in c["steps"] if s["op"] == "postEvent")
        target = next(a["target"] for a in doc["events"][str(play["args"][0])] if a["type"] == 4)
        snd = first_sound(nodes, target)
        if snd and bus in bus_chain(nodes, snd):
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


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--cases", type=int, default=240)
    ap.add_argument("--length", type=float, default=4.0)
    args = ap.parse_args()
    doc = json.loads((Path(build_audio.OUT) / "audio.json").read_text(encoding="utf-8"))
    OUT.mkdir(parents=True, exist_ok=True)
    WORK.mkdir(parents=True, exist_ok=True)
    build_audio_ref()
    length = int(args.length * RATE)
    for bus, name in REVERB_BUSSES.items():
        print(f"== {name} ({bus})")
        cases, reset = select_cases(doc, bus, args.cases, args.length)
        cases_file = WORK / f"{bus}.json"
        cases_file.write_text(json.dumps({"reset": reset, "cases": cases}), encoding="utf-8")
        master = [(MASTER, 0), (MASTER, 1)]
        run(cases_file, WORK / "wet", master)
        run(cases_file, WORK / "dry", master + [(bus, 0), (bus, 1)])
        pairs = []
        for c in cases:
            wet = audiodiff_features.read_wav((WORK / "wet" / f"{c['id']}.wav").read_bytes())
            dry = audiodiff_features.read_wav((WORK / "dry" / f"{c['id']}.wav").read_bytes())
            n = min(len(wet), len(dry))
            if np.abs(dry[:n]).max() > 1e-3:
                pairs.append((wet[:n].astype(np.float64), dry[:n].astype(np.float64)))
        train, test = pairs[::2] + pairs[3::4], pairs[1::4]
        h = estimate(train, length)
        print(f"   {len(pairs)} usable cases; dry*h vs. wet on {len(test)} held-out cases: "
              f"residual {predict_error(test, h):.1f} dB (plain dry: {predict_error(test, np.eye(1, length).T.repeat(2, 1)):.1f} dB)")
        h = estimate(pairs, length)
        peak = np.argmax(np.abs(h).max(axis=1))
        print(f"   direct path at {peak / RATE * 1000:.1f} ms, tail energy (after 20 ms) "
              f"{10 * np.log10(np.sum(h[int(0.02 * RATE):] ** 2) / np.sum(h ** 2)):.1f} dB of total")
        write_float_wav(OUT / f"{bus}.wav", h)
        print("  ", OUT / f"{bus}.wav")
    shutil.rmtree(WORK, ignore_errors=True)


def build_audio_ref():
    subprocess.run(["dotnet", "build", str(PORT_DIR / "WwiseRef"), "-c", "Release", "-o", str(PORT_DIR / "build" / "WwiseRef"),
                    "-nologo", "-v", "q"], check=True, stdout=subprocess.DEVNULL)


if __name__ == "__main__":
    main()
